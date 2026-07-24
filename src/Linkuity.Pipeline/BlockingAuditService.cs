using Linkuity.Core.Models;
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

    private readonly IStrategyRegistry _registry;

    public BlockingAuditService(IStrategyRegistry registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public BlockingAuditResult Audit(
        IReadOnlyList<EntityRecord> records,
        MatchingProfile profile,
        IReadOnlyDictionary<string, string>? groundTruth = null,
        int? maxCandidates = null)
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

        // 3. Candidate pairs (distinct unordered record pairs sharing >=1 key) + connected records.
        var candidatePairs = new HashSet<(string, string)>();
        var connected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in blocks)
        {
            if (block.Size < 2) continue;
            var ids = block.MemberSourceRecordIds;
            for (var i = 0; i < ids.Count; i++)
                for (var j = i + 1; j < ids.Count; j++)
                {
                    var (lo, hi) = string.CompareOrdinal(ids[i], ids[j]) <= 0 ? (ids[i], ids[j]) : (ids[j], ids[i]);
                    candidatePairs.Add((lo, hi));
                    connected.Add(lo);
                    connected.Add(hi);
                }
        }

        var structural = new BlockingStructuralStats(
            TotalBlocks: blocks.Count,
            SingletonRecordCount: records.Count - connected.Count,
            TotalCandidatePairs: candidatePairs.Count,
            MaxBlockSize: blocks.Count > 0 ? blocks[0].Size : 0,
            MeanBlockSize: blocks.Count > 0 ? blocks.Average(b => b.Size) : 0,
            LargestBlocks: blocks.Take(LargestBlocksCount).ToList());

        var capHazards = maxCandidates is { } cap
            ? blocks.Where(b => b.Size > cap).ToList()
            : (IReadOnlyList<BlockingBlock>)[];

        // Reachability is filled in Task 2.
        BlockingReachabilityReport? reachability = null;

        return new BlockingAuditResult(
            records.Count, profile.BlockingStrategies.ToList(), perRecord, blocks, structural, capHazards, reachability);
    }
}
