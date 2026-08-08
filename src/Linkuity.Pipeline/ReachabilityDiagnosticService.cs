using Linkuity.Core.Models;
using Linkuity.Matching.Canonicalization;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;

namespace Linkuity.Pipeline;

/// <summary>
/// Classifies WHY the engine never compares a true (ground-truth) pair: cause A (every shared
/// key was suppressed by maxBlockSize), cause B1 (a declared-Blocking field shares a value but no
/// strategy can key it -- a capability gap), cause B2 (an undeclared corpus column shares a value
/// -- a configuration gap), or cause B3 (genuinely disjoint). Normalization loss is reported as an
/// orthogonal flag, since it can accompany either an A or a B classification.
///
/// Built directly on <see cref="BlockingKeyIndex"/> -- the same interned, scale-safe primitives
/// <see cref="BlockingAuditService"/> uses -- so this is the instrument that runs at full corpus
/// scale (3.9M records), not a sample-scale companion to it.
/// </summary>
public sealed class ReachabilityDiagnosticService
{
    private const int CauseSampleCap = 50;
    private const int LargestBlockCount = 10;
    private const string ProbeValue = "PROBE-VALUE-1";

    private static readonly OrganizationNameCanonicalizer OrgCanonicalizer = new();

    private readonly IStrategyRegistry _registry;

    public ReachabilityDiagnosticService(IStrategyRegistry registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public ReachabilityDiagnosticResult Diagnose(
        IReadOnlyList<EntityRecord> records,
        MatchingProfile profile,
        IReadOnlyDictionary<string, string> groundTruth,
        int? maxBlockSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(groundTruth);

        var index = BlockingKeyIndex.Build(records, profile, _registry, ct);
        var suppressed = BlockingKeyIndex.SuppressedKeys(index, maxBlockSize);

        var bySource = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < records.Count; i++) bySource[records[i].SourceRecordId] = i;

        var declaredFieldNames = profile.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var undeclaredColumns = records
            .SelectMany(r => r.Fields.Keys)
            .Where(c => !declaredFieldNames.Contains(c))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
        var unusableBlockingFields = ComputeUnusableBlockingFields(profile, _registry);

        // Ground-truth groups, restricted to records actually present in this record set.
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (sourceId, canonical) in groundTruth)
        {
            if (!bySource.ContainsKey(sourceId)) continue;
            (groups.TryGetValue(canonical, out var list) ? list : groups[canonical] = []).Add(sourceId);
        }

        long truePairs = 0, reachablePairs = 0;
        long aCount = 0, b1Count = 0, b2Count = 0, b3Count = 0;
        long normImplicatedCount = 0, legalSuffixOnlyCount = 0;

        var b1ByColumn = new Dictionary<string, long>(StringComparer.Ordinal);
        var b2ByColumn = new Dictionary<string, long>(StringComparer.Ordinal);
        var aDetailCounts = new Dictionary<(string Strategy, int BlockSize), long>();
        var owningStrategyCache = new Dictionary<int, string>();

        var aSampler = new CappedPairSampler(CauseSampleCap);
        var b1Sampler = new CappedPairSampler(CauseSampleCap);
        var b2Sampler = new CappedPairSampler(CauseSampleCap);
        var b3Sampler = new CappedPairSampler(CauseSampleCap);
        var normSampler = new CappedPairSampler(CauseSampleCap);

        // Iterate true pairs GROUP BY GROUP: never materialise a per-pair collection over the
        // whole ground truth. The only retained structures are the counters above, the
        // ByColumn/CauseADetail dictionaries (bounded by column count and by strategy count x
        // distinct block sizes respectively), the owning-strategy cache (bounded by distinct key
        // count), and the five capped samplers.
        foreach (var (canonical, membersRaw) in groups)
        {
            if (membersRaw.Count < 2) continue;
            var members = membersRaw.OrderBy(x => x, StringComparer.Ordinal).ToList();

            for (var i = 0; i < members.Count; i++)
            {
                for (var j = i + 1; j < members.Count; j++)
                {
                    ct.ThrowIfCancellationRequested();
                    truePairs++;

                    var li = bySource[members[i]];
                    var ri = bySource[members[j]];
                    var left = records[li];
                    var right = records[ri];
                    var leftKeys = index.RecordKeys[li];
                    var rightKeys = index.RecordKeys[ri];

                    if (BlockingKeyIndex.SharesAnyActiveKey(leftKeys, rightKeys, suppressed))
                    {
                        reachablePairs++;
                    }
                    else
                    {
                        var sharedIgnoringSuppression = BlockingKeyIndex.SharedKeysIgnoringSuppression(leftKeys, rightKeys);
                        if (sharedIgnoringSuppression.Count > 0)
                        {
                            // Cause A: every key both records carry was thrown away by the cap.
                            aCount++;
                            aSampler.Offer(members[i], members[j], canonical);
                            foreach (var keyId in sharedIgnoringSuppression)
                            {
                                var strategy = OwningStrategyOf(keyId, index, records, profile, _registry, owningStrategyCache);
                                var blockSize = index.KeyCount[keyId];
                                var detailKey = (strategy, blockSize);
                                aDetailCounts[detailKey] = aDetailCounts.GetValueOrDefault(detailKey) + 1;
                            }
                        }
                        else
                        {
                            // No shared key at all (active or suppressed). Classify B1 BEFORE B2:
                            // a field the profile declares Blocking but that no strategy can key
                            // is a capability gap regardless of what else is undeclared on the
                            // same pair. Checking B2 first would attribute a capability gap to
                            // configuration and understate the real problem -- "add a profile
                            // line" instead of "build a capability".
                            var b1Matches = FindUnusableBlockingFieldMatches(left, right, profile, unusableBlockingFields);
                            if (b1Matches.Count > 0)
                            {
                                b1Count++;
                                foreach (var column in b1Matches)
                                    b1ByColumn[column] = b1ByColumn.GetValueOrDefault(column) + 1;
                                b1Sampler.Offer(members[i], members[j], canonical);
                            }
                            else
                            {
                                var b2Matches = FindUndeclaredColumnMatches(left, right, undeclaredColumns);
                                if (b2Matches.Count > 0)
                                {
                                    b2Count++;
                                    foreach (var column in b2Matches)
                                        b2ByColumn[column] = b2ByColumn.GetValueOrDefault(column) + 1;
                                    b2Sampler.Offer(members[i], members[j], canonical);
                                }
                                else
                                {
                                    b3Count++;
                                    b3Sampler.Offer(members[i], members[j], canonical);
                                }
                            }
                        }

                        // Normalization loss is orthogonal to the A/B classification above: it
                        // flags pairs whose organization-name fields share a raw token (e.g. a
                        // legal suffix) that canonicalization removed before any blocking
                        // strategy saw it. It can apply to a B pair (nothing else shared either)
                        // or, in principle, alongside cause A.
                        if (IsNormalizationImplicated(left, right, profile, out var legalSuffixOnly))
                        {
                            normImplicatedCount++;
                            if (legalSuffixOnly) legalSuffixOnlyCount++;
                            normSampler.Offer(members[i], members[j], canonical);
                        }
                    }
                }
            }
        }

        var unreachablePairs = aCount + b1Count + b2Count + b3Count;
        AssertReconciles(truePairs, reachablePairs, unreachablePairs, aCount, b1Count, b2Count, b3Count);

        var causeADetail = aDetailCounts
            .OrderBy(kv => kv.Key.Strategy, StringComparer.Ordinal)
            .ThenBy(kv => kv.Key.BlockSize)
            .Select(kv => new SuppressedKeyDetail(kv.Key.Strategy, kv.Key.BlockSize, kv.Value))
            .ToList();

        var causeA = new CauseTally(aCount, new Dictionary<string, long>(), aSampler.ToSortedList());
        var causeB1 = new CauseTally(b1Count, SortedColumns(b1ByColumn), b1Sampler.ToSortedList());
        var causeB2 = new CauseTally(b2Count, SortedColumns(b2ByColumn), b2Sampler.ToSortedList());
        var causeB3 = new CauseTally(b3Count, new Dictionary<string, long>(), b3Sampler.ToSortedList());
        var normalization = new NormalizationTally(normImplicatedCount, legalSuffixOnlyCount, normSampler.ToSortedList());

        var blocks = BuildBlockHistogram(index, records, profile, _registry, owningStrategyCache);

        return new ReachabilityDiagnosticResult(
            truePairs,
            reachablePairs,
            unreachablePairs,
            causeA,
            causeB1,
            causeB2,
            causeB3,
            normalization,
            causeADetail,
            Unreachable: new FieldCoOccurrenceSet(0, new Dictionary<string, FieldCoOccurrence>()),
            Control: new ControlSet(0, 0, new Dictionary<string, FieldCoOccurrence>()),
            Blocks: blocks);
    }

    /// <summary>Fails the run if the cause tallies do not account for every pair. The corpus
    /// build's final review found its equivalent arithmetic living in review prose rather than
    /// in code; a future silent skip must trip an assertion, not depend on someone noticing.</summary>
    internal static void AssertReconciles(
        long truePairs, long reachable, long unreachable, long a, long b1, long b2, long b3)
    {
        if (reachable + unreachable != truePairs)
            throw new InvalidOperationException(
                $"reachable {reachable:N0} + unreachable {unreachable:N0} != truePairs {truePairs:N0}");
        if (a + b1 + b2 + b3 != unreachable)
            throw new InvalidOperationException(
                $"A {a:N0} + B1 {b1:N0} + B2 {b2:N0} + B3 {b3:N0} != unreachable {unreachable:N0}");
    }

    private static IReadOnlyDictionary<string, long> SortedColumns(Dictionary<string, long> byColumn)
        => byColumn.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    /// <summary>A Blocking-role field is "unusable" when NONE of the profile's configured
    /// blocking strategies would emit any key from it -- checked once, up front, against a
    /// synthetic probe record carrying only that field, so the result depends purely on
    /// capability (semantic type x configured strategy set), never on the pair's actual values.
    /// </summary>
    private static HashSet<string> ComputeUnusableBlockingFields(MatchingProfile profile, IStrategyRegistry registry)
    {
        var unusable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in profile.Fields)
        {
            if (!field.Roles.HasFlag(FieldRole.Blocking)) continue;
            var probe = ProbeRecord(field.Name);
            var keyed = profile.BlockingStrategies.Any(name =>
                registry.Blocking[name].GenerateKeys(probe, profile).Count > 0);
            if (!keyed) unusable.Add(field.Name);
        }
        return unusable;
    }

    private static EntityRecord ProbeRecord(string fieldName) => new()
    {
        Id = Guid.Empty, ProjectId = Guid.Empty, SourceId = Guid.Empty, IngestBatchId = Guid.Empty,
        SourceRecordId = "probe",
        Fields = new Dictionary<string, string> { [fieldName] = ProbeValue },
        CreatedAt = DateTimeOffset.UnixEpoch
    };

    /// <summary>Every profile-declared Blocking field that is capability-unusable AND has an
    /// equal, non-empty value on both records. Returns every matching column (not just the
    /// first) so CauseB1.ByColumn reflects all of them; classification only needs Count > 0.
    /// </summary>
    private static List<string> FindUnusableBlockingFieldMatches(
        EntityRecord left, EntityRecord right, MatchingProfile profile, IReadOnlySet<string> unusableBlockingFields)
    {
        var matches = new List<string>();
        foreach (var field in profile.Fields)
        {
            if (!field.Roles.HasFlag(FieldRole.Blocking)) continue;
            if (!unusableBlockingFields.Contains(field.Name)) continue;
            if (ValuesEqual(left.Fields.GetValueOrDefault(field.Name), right.Fields.GetValueOrDefault(field.Name)))
                matches.Add(field.Name);
        }
        return matches;
    }

    /// <summary>Every corpus column the profile does NOT declare that has an equal, non-empty
    /// value on both records.</summary>
    private static List<string> FindUndeclaredColumnMatches(
        EntityRecord left, EntityRecord right, IReadOnlyList<string> undeclaredColumns)
    {
        var matches = new List<string>();
        foreach (var column in undeclaredColumns)
            if (ValuesEqual(left.Fields.GetValueOrDefault(column), right.Fields.GetValueOrDefault(column)))
                matches.Add(column);
        return matches;
    }

    private static bool ValuesEqual(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
           && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the pair's organization-name field(s) share a RAW token (kept-suffix
    /// canonical form) that the fully-stripped canonical form no longer shares -- i.e. suffix
    /// stripping is why the tokens diverged. legalSuffixOnly is set when every such lost token is
    /// a recognised legal suffix (ORGANIZATIONNAMECANONICALIZER.IsLegalSuffix), isolating "the
    /// suffix list cost a match" from "the suffix list is fine, something else differed".</summary>
    private static bool IsNormalizationImplicated(
        EntityRecord left, EntityRecord right, MatchingProfile profile, out bool legalSuffixOnly)
    {
        legalSuffixOnly = false;
        var implicatedAny = false;

        foreach (var field in profile.Fields)
        {
            if (field.SemanticType != SemanticFieldType.OrganizationName) continue;
            if (!left.Fields.TryGetValue(field.Name, out var leftValue) || string.IsNullOrWhiteSpace(leftValue)) continue;
            if (!right.Fields.TryGetValue(field.Name, out var rightValue) || string.IsNullOrWhiteSpace(rightValue)) continue;

            var rawLeft = OrgCanonicalizer.CanonicalizeKeepingSuffixes(leftValue).ToHashSet(StringComparer.Ordinal);
            var rawRight = OrgCanonicalizer.CanonicalizeKeepingSuffixes(rightValue).ToHashSet(StringComparer.Ordinal);
            var sharedRaw = rawLeft.Intersect(rawRight, StringComparer.Ordinal).ToList();
            if (sharedRaw.Count == 0) continue;

            var sharedCanonical = OrgCanonicalizer.Canonicalize(leftValue)
                .ToHashSet(StringComparer.Ordinal)
                .Intersect(OrgCanonicalizer.Canonicalize(rightValue), StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);

            var lostToStripping = sharedRaw.Where(t => !sharedCanonical.Contains(t)).ToList();
            if (lostToStripping.Count == 0) continue;

            implicatedAny = true;
            if (lostToStripping.All(OrganizationNameCanonicalizer.IsLegalSuffix))
                legalSuffixOnly = true;
        }

        return implicatedAny;
    }

    /// <summary>Which configured strategy emitted a given interned key, resolved lazily and
    /// cached by key id (bounded by distinct key count, not corpus size). Picks the first
    /// strategy, in PROFILE order (a List, not a Dictionary/HashSet), whose output for one member
    /// record contains the key string -- deterministic regardless of process or hash seed.
    /// </summary>
    private static string OwningStrategyOf(
        int keyId, KeyIndex index, IReadOnlyList<EntityRecord> records, MatchingProfile profile,
        IStrategyRegistry registry, Dictionary<int, string> cache)
    {
        if (cache.TryGetValue(keyId, out var cached)) return cached;

        var normalization = registry.Normalization[profile.NormalizationStrategy];
        var memberRecordIndex = index.KeyMembers[keyId][0];
        var normalized = normalization.Normalize(records[memberRecordIndex], profile);
        var keyName = index.KeyNames[keyId];

        var owner = "unknown";
        foreach (var strategyName in profile.BlockingStrategies)
        {
            var keys = registry.Blocking[strategyName].GenerateKeys(normalized, profile);
            if (keys.Contains(keyName, StringComparer.Ordinal))
            {
                owner = strategyName;
                break;
            }
        }
        cache[keyId] = owner;
        return owner;
    }

    /// <summary>Block-size distribution built straight from the interned index -- bounded by
    /// distinct-key count, not corpus size, so it survives at 3.9M records where
    /// BlockingAuditService's string-keyed structures would not. Largest blocks are capped to the
    /// top N, tie-broken by key name (ordinal) so the cap never depends on array/dictionary
    /// enumeration order.</summary>
    private static BlockSizeHistogram BuildBlockHistogram(
        KeyIndex index, IReadOnlyList<EntityRecord> records, MatchingProfile profile,
        IStrategyRegistry registry, Dictionary<int, string> owningStrategyCache)
    {
        var buckets = new SortedDictionary<int, (int Count, long Slots)>();
        var maxSize = 0;
        for (var k = 0; k < index.KeyCount.Length; k++)
        {
            var size = index.KeyCount[k];
            if (size > maxSize) maxSize = size;
            var bucketIndex = size <= 1 ? 0 : (int)Math.Ceiling(Math.Log2(size));
            var (count, slots) = buckets.TryGetValue(bucketIndex, out var agg) ? agg : (0, 0L);
            buckets[bucketIndex] = (count + 1, slots + size);
        }

        var bucketList = buckets.Select(kv =>
        {
            var (min, max) = kv.Key == 0 ? (1, 1) : ((1 << (kv.Key - 1)) + 1, 1 << kv.Key);
            return new BlockingSizeBucket(min, max, kv.Value.Count, kv.Value.Slots);
        }).ToList();

        var largest = Enumerable.Range(0, index.KeyCount.Length)
            .OrderByDescending(k => index.KeyCount[k])
            .ThenBy(k => index.KeyNames[k], StringComparer.Ordinal)
            .Take(LargestBlockCount)
            .Select(k => new LargestBlock(
                index.KeyNames[k],
                OwningStrategyOf(k, index, records, profile, registry, owningStrategyCache),
                index.KeyCount[k]))
            .ToList();

        return new BlockSizeHistogram(bucketList, index.KeyCount.Length, maxSize, largest);
    }

    /// <summary>Deterministic bounded sample, ranked by a stable hash of the pair's ids with an
    /// ordinal tie-break -- same technique as BlockingAuditService.MissedPairSampler and for the
    /// same reason: ground truth is an IReadOnlyDictionary, so encounter order follows Dictionary
    /// iteration, which is not stable across runs. "First N encountered" would make the sample
    /// noise rather than a sample. Reuses MissedPairSampler's proven stable hash rather than a
    /// second, potentially-divergent implementation.</summary>
    private sealed class CappedPairSampler(int cap)
    {
        private readonly record struct RankKey(uint Rank, string Left, string Right) : IComparable<RankKey>
        {
            public int CompareTo(RankKey other)
            {
                var cmp = Rank.CompareTo(other.Rank);
                if (cmp != 0) return cmp;
                cmp = StringComparer.Ordinal.Compare(Left, other.Left);
                return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(Right, other.Right);
            }
        }

        private static readonly IComparer<RankKey> WorstFirst = Comparer<RankKey>.Create((a, b) => b.CompareTo(a));
        private readonly PriorityQueue<SampledPair, RankKey> _queue = new(WorstFirst);

        internal void Offer(string left, string right, string canonicalKey)
        {
            var rank = BlockingAuditService.MissedPairSampler.Rank(left, right);
            var key = new RankKey(rank, left, right);
            if (_queue.Count < cap)
            {
                _queue.Enqueue(new SampledPair(left, right, canonicalKey), key);
                return;
            }
            _queue.TryPeek(out _, out var worst);
            if (key.CompareTo(worst) >= 0) return;
            _queue.Enqueue(new SampledPair(left, right, canonicalKey), key);
            _queue.Dequeue();
        }

        internal IReadOnlyList<SampledPair> ToSortedList()
            => [.. _queue.UnorderedItems
                    .Select(x => x.Element)
                    .OrderBy(p => p.CanonicalKey, StringComparer.Ordinal)
                    .ThenBy(p => p.LeftSourceRecordId, StringComparer.Ordinal)
                    .ThenBy(p => p.RightSourceRecordId, StringComparer.Ordinal)];
    }
}
