using Linkuity.Core.Models;
using Linkuity.Matching.Blocking;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;

namespace Linkuity.Pipeline;

/// <summary>
/// Analyzes blocking quality for a matching profile over a record set: per-strategy key
/// attribution, block structure, candidate-pair workload, MaxCandidates truncation hazards,
/// and (with ground truth) the blocking recall ceiling with missed-pair diagnostics.
/// Pure and I/O-free; the CLI supplies records from a CSV or a durable store. Keys are always
/// recomputed from the profile under test (stored BlockingKeys are ignored) so a candidate
/// blocking config can be evaluated without re-ingesting.
/// </summary>
public sealed class BlockingAuditService
{
    private const int LargestBlocksCount = 10;
    private static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>Missed pairs retained for inspection. The COUNT (<see cref="BlockingReachabilityReport.MissedPairCount"/>)
    /// is exact and unbounded; the sample (<see cref="BlockingReachabilityReport.MissedPairs"/>) is
    /// capped so memory does not scale with corpus size.</summary>
    public const int MissedPairSampleCap = 500;

    private readonly IStrategyRegistry _registry;

    public BlockingAuditService(IStrategyRegistry registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public BlockingAuditResult Audit(
        IReadOnlyList<EntityRecord> records,
        MatchingProfile profile,
        IReadOnlyDictionary<string, string>? groundTruth = null,
        int? maxCandidates = null,
        int? maxBlockSize = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(profile);

        var normalization = _registry.Normalization[profile.NormalizationStrategy];

        // 1. Per-record, per-strategy keys, computed on the NORMALIZED record (mirrors the engine).
        var perRecord = new List<RecordBlocking>(records.Count);
        var bySource = new Dictionary<string, RecordBlocking>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            var normalized = normalization.Normalize(record, profile);
            var byStrategy = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var all = new HashSet<string>(KeyComparer);
            foreach (var strategyName in profile.BlockingStrategies)
            {
                var keys = _registry.Blocking[strategyName]
                    .GenerateKeys(normalized, profile)
                    .Distinct(KeyComparer)
                    .OrderBy(k => k, KeyComparer)
                    .ToList();
                byStrategy[strategyName] = keys;
                foreach (var k in keys) all.Add(k);
            }
            var rb = new RecordBlocking(record.SourceRecordId, byStrategy, all.OrderBy(k => k, KeyComparer).ToList());
            perRecord.Add(rb);
            bySource[record.SourceRecordId] = rb;
        }

        // 2. Inverted index: key -> member records + emitting strategies.
        var members = new Dictionary<string, SortedSet<string>>(KeyComparer);
        var keyStrategies = new Dictionary<string, SortedSet<string>>(KeyComparer);
        foreach (var rb in perRecord)
            foreach (var (strategyName, keys) in rb.KeysByStrategy)
                foreach (var key in keys)
                {
                    (members.TryGetValue(key, out var m) ? m : members[key] = new SortedSet<string>(StringComparer.Ordinal)).Add(rb.SourceRecordId);
                    (keyStrategies.TryGetValue(key, out var s) ? s : keyStrategies[key] = new SortedSet<string>(StringComparer.Ordinal)).Add(strategyName);
                }

        var blocks = members
            .Select(kv => new BlockingBlock(kv.Key, keyStrategies[kv.Key].ToList(), kv.Value.ToList()))
            .OrderByDescending(b => b.Size)
            .ThenBy(b => b.Key, KeyComparer)
            .ToList();

        // 3. Candidate pairs (distinct unordered record pairs sharing >=1 key), counted via the
        // shared ownership walk instead of a HashSet<(string,string)>: that set held one entry per
        // distinct pair -- 33.2M string tuples on the gate corpus, the single most severe unbounded
        // structure this service used to carry. ForEachCandidatePair already emits each pair
        // exactly once (lowest-shared-key ownership), so a running counter is sufficient; no dedup
        // structure is built at all. "connected" becomes a bool[] sized to the record count (never
        // a set of strings) purely to derive SingletonRecordCount.
        var index = BlockingKeyIndex.Build(records, profile, _registry);
        var connected = new bool[records.Count];
        long distinctPairs = 0;
        var visits = BlockingKeyIndex.ForEachCandidatePair(index, maxBlockSize: null, onPair: (a, b) =>
        {
            distinctPairs++;
            connected[a] = true;
            connected[b] = true;
        });
        var connectedCount = 0;
        for (var i = 0; i < connected.Length; i++)
            if (connected[i]) connectedCount++;

        var structural = new BlockingStructuralStats(
            TotalBlocks: blocks.Count,
            SingletonRecordCount: records.Count - connectedCount,
            TotalCandidatePairs: distinctPairs,
            TotalBlockPairVisits: visits,
            MaxBlockSize: blocks.Count > 0 ? blocks[0].Size : 0,
            MeanBlockSize: blocks.Count > 0 ? blocks.Average(b => b.Size) : 0,
            LargestBlocks: blocks.Take(LargestBlocksCount).ToList(),
            SizeHistogram: BuildSizeHistogram(blocks));

        var capHazards = maxCandidates is { } cap
            ? blocks.Where(b => b.Size > cap).ToList()
            : (IReadOnlyList<BlockingBlock>)[];

        var reachability = groundTruth is null
            ? null
            : ComputeReachability(bySource, profile.BlockingStrategies, groundTruth);

        BlockingSuppressionReport? suppression = null;
        if (maxBlockSize is { } max)
        {
            var policy = new BlockingKeySuppressionPolicy(max);
            // Engine parity: blocking-linear counts key frequency over a corpus that
            // excludes the query record, so a full block of size S has per-query
            // frequency S-1. A block of exactly max+1 stays active.
            var suppressedBlocks = blocks.Where(b => policy.IsSuppressed(b.Key, b.Size - 1)).ToList();
            var suppressedKeys = suppressedBlocks.Select(b => b.Key).ToHashSet(KeyComparer);

            var noActive = perRecord
                .Where(rb => rb.AllKeys.Count > 0 && rb.AllKeys.All(suppressedKeys.Contains))
                .Select(rb => rb.SourceRecordId)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            BlockingReachabilityReport? effective = null;
            if (groundTruth is not null)
            {
                var filteredBySource = bySource.ToDictionary(
                    kv => kv.Key,
                    kv => FilterSuppressed(kv.Value, suppressedKeys),
                    StringComparer.Ordinal);
                effective = ComputeReachability(filteredBySource, profile.BlockingStrategies, groundTruth);
            }

            suppression = new BlockingSuppressionReport(max, suppressedBlocks, noActive, effective);
        }

        return new BlockingAuditResult(
            records.Count, profile.BlockingStrategies.ToList(), perRecord, blocks, structural, capHazards, reachability, suppression);
    }

    private static RecordBlocking FilterSuppressed(RecordBlocking rb, HashSet<string> suppressedKeys)
        => new(
            rb.SourceRecordId,
            rb.KeysByStrategy.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<string>)kv.Value.Where(k => !suppressedKeys.Contains(k)).ToList(),
                StringComparer.Ordinal),
            rb.AllKeys.Where(k => !suppressedKeys.Contains(k)).ToList());

    private static BlockingReachabilityReport ComputeReachability(
        IReadOnlyDictionary<string, RecordBlocking> bySource,
        IReadOnlyList<string> strategyNames,
        IReadOnlyDictionary<string, string> groundTruth)
    {
        // Group present, labeled records by their canonical (true-entity) key.
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (sourceId, canonical) in groundTruth)
        {
            if (!bySource.ContainsKey(sourceId)) continue; // labeled record not in this record set
            (groups.TryGetValue(canonical, out var list) ? list : groups[canonical] = new List<string>()).Add(sourceId);
        }

        var truePairs = 0;
        var reachablePairs = 0;
        var missedCount = 0;
        var sampler = new MissedPairSampler();
        var contributed = new Dictionary<string, int>(StringComparer.Ordinal);
        var unique = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (canonical, membersRaw) in groups)
        {
            var members = membersRaw.OrderBy(x => x, StringComparer.Ordinal).ToList();
            for (var i = 0; i < members.Count; i++)
                for (var j = i + 1; j < members.Count; j++)
                {
                    truePairs++;
                    var left = bySource[members[i]];
                    var right = bySource[members[j]];
                    var shared = left.AllKeys.Intersect(right.AllKeys, KeyComparer).ToList();
                    if (shared.Count == 0)
                    {
                        missedCount++;
                        // Construct the MissedPair (it carries BOTH records' full key lists) only
                        // when the sampler will actually retain it -- building one per unreachable
                        // pair and discarding it would defeat the point of capping the sample. The
                        // key (rank + ids) is computed once here and threaded through both calls,
                        // rather than recomputed inside Offer.
                        var key = new MissedPairSampler.SampleKey(
                            MissedPairSampler.Rank(members[i], members[j]), members[i], members[j]);
                        if (sampler.WouldKeep(key))
                            sampler.Offer(new MissedPair(members[i], members[j], canonical, left.AllKeys, right.AllKeys), key);
                        continue;
                    }
                    reachablePairs++;

                    // Which strategies carry a shared key on BOTH sides.
                    var perSharedKeyStrategies = new List<HashSet<string>>(shared.Count);
                    var carrying = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var key in shared)
                    {
                        var carriers = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var name in strategyNames)
                            if (left.KeysByStrategy[name].Contains(key, KeyComparer) &&
                                right.KeysByStrategy[name].Contains(key, KeyComparer))
                                carriers.Add(name);
                        perSharedKeyStrategies.Add(carriers);
                        foreach (var c in carriers) carrying.Add(c);
                    }

                    foreach (var s in carrying)
                    {
                        contributed[s] = contributed.GetValueOrDefault(s) + 1;
                        // Load-bearing: without s, no shared key is carried by any other strategy.
                        var survivesWithoutS = perSharedKeyStrategies.Any(set => set.Any(x => x != s));
                        if (!survivesWithoutS)
                            unique[s] = unique.GetValueOrDefault(s) + 1;
                    }
                }
        }

        var attribution = strategyNames
            .Select(s => new StrategyAttribution(s, contributed.GetValueOrDefault(s), unique.GetValueOrDefault(s)))
            .ToList();
        var recall = truePairs == 0 ? 0.0 : (double)reachablePairs / truePairs;

        // Deterministic order: pairs were offered to the sampler while enumerating a Dictionary,
        // so encounter order is not guaranteed stable across runtimes. The CSV formatter emits
        // these rows for diffing runs across config changes.
        var missedPairs = sampler.ToSortedList();

        return new BlockingReachabilityReport(truePairs, reachablePairs, recall, missedCount, missedPairs, attribution);
    }

    /// <summary>How many blocks fall in each power-of-two size bucket ([1,1], [2,2], [3,4], [5,8],
    /// ...): bounded by bucket count rather than by corpus size, so it survives retaining every
    /// block does not. Every block lands in exactly one bucket, so bucket counts always sum to
    /// blocks.Count.</summary>
    private static IReadOnlyList<BlockingSizeBucket> BuildSizeHistogram(IReadOnlyList<BlockingBlock> blocks)
    {
        var buckets = new SortedDictionary<int, (int Count, long Slots)>();
        foreach (var block in blocks)
        {
            var size = block.Size;
            var bucketIndex = size <= 1 ? 0 : (int)Math.Ceiling(Math.Log2(size));
            var (count, slots) = buckets.TryGetValue(bucketIndex, out var agg) ? agg : (0, 0L);
            buckets[bucketIndex] = (count + 1, slots + size);
        }

        return buckets.Select(kv =>
        {
            var (min, max) = kv.Key == 0 ? (1, 1) : ((1 << (kv.Key - 1)) + 1, 1 << kv.Key);
            return new BlockingSizeBucket(min, max, kv.Value.Count, kv.Value.Slots);
        }).ToList();
    }

    /// <summary>
    /// Deterministic bounded sample of missed pairs: retains the (at most) MissedPairSampleCap
    /// pairs with the smallest <see cref="SampleKey"/>. Ranked by a TOTAL order over (hash, left
    /// id, right id) rather than "the first N encountered", for two independent reasons:
    /// <list type="bullet">
    /// <item>encounter order follows Dictionary iteration, which is not stable across runtimes --
    /// the same corpus would yield a different sample on a different machine, which is not a
    /// sample, it is noise;</item>
    /// <item>rank ALONE is only a 32-bit hash: with hundreds of thousands of missed pairs a
    /// collision at the cap boundary is near-certain by the birthday bound, and without an ordinal
    /// tie-break, which of two equally-ranked pairs survives would once again depend on arrival
    /// order into <see cref="Offer"/> -- the exact instability removed from ranking, reintroduced
    /// one level up.</item>
    /// </list>
    /// Ordinal comparison of the ids makes the ordering total (record-id pairs are unique within
    /// one run, so no two SampleKeys can tie), so the retained set is fully determined by content,
    /// independent of both the process's hash seed and Dictionary iteration order. Internal (not
    /// private) so BlockingAuditServiceTests can pin the hash function directly and exercise
    /// tie-breaking without needing 500+ pairs.
    /// </summary>
    internal sealed class MissedPairSampler(int cap = MissedPairSampleCap)
    {
        private readonly int _cap = cap;
        private readonly PriorityQueue<MissedPair, SampleKey> _worstFirst = new(WorstFirstComparer);

        private static readonly IComparer<SampleKey> WorstFirstComparer =
            Comparer<SampleKey>.Create((a, b) => b.CompareTo(a));   // reversed: largest key dequeues first

        /// <summary>Total order a pair is sampled by: rank first, then an ordinal tie-break over
        /// the record ids. Record-id pairs are unique within one run, so this ordering never ties
        /// -- which pair is retained can never depend on the order pairs were offered in.</summary>
        internal readonly record struct SampleKey(uint Rank, string Left, string Right) : IComparable<SampleKey>
        {
            public int CompareTo(SampleKey other)
            {
                var cmp = Rank.CompareTo(other.Rank);
                if (cmp != 0) return cmp;
                cmp = StringComparer.Ordinal.Compare(Left, other.Left);
                return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(Right, other.Right);
            }
        }

        // NOT string.GetHashCode()/HashCode.Combine: .NET randomizes the default string hash with
        // a per-process seed (DoS mitigation), so it is stable within one process run but differs
        // across separate runs and machines -- silently reintroducing exactly the
        // "different sample on a different machine" noise this sampler exists to avoid. FNV-1a
        // over the raw characters has no such seed, so the same corpus ranks the same pair
        // identically on every run, everywhere.
        internal static uint Rank(string left, string right)
            => unchecked(StableHash(left) * 31 + StableHash(right));

        internal static uint StableHash(string value)
        {
            var hash = 2166136261u;
            foreach (var c in value)
                unchecked
                {
                    hash ^= c;
                    hash *= 16777619u;
                }
            return hash;
        }

        /// <summary>Whether offering a pair with this key would actually be retained, checked
        /// BEFORE the (expensive) MissedPair is constructed.</summary>
        internal bool WouldKeep(SampleKey key)
        {
            if (_worstFirst.Count < _cap) return true;
            _worstFirst.TryPeek(out _, out var worst);
            return key.CompareTo(worst) < 0;
        }

        /// <summary>Takes the SampleKey the caller already computed for WouldKeep, rather than
        /// recomputing the rank from the pair's ids here.</summary>
        internal void Offer(MissedPair pair, SampleKey key)
        {
            _worstFirst.Enqueue(pair, key);
            if (_worstFirst.Count > _cap) _worstFirst.Dequeue();
        }

        internal IReadOnlyList<MissedPair> ToSortedList()
            => [.. _worstFirst.UnorderedItems
                    .Select(x => x.Element)
                    .OrderBy(m => m.CanonicalKey, StringComparer.Ordinal)
                    .ThenBy(m => m.LeftSourceRecordId, StringComparer.Ordinal)
                    .ThenBy(m => m.RightSourceRecordId, StringComparer.Ordinal)];
    }
}
