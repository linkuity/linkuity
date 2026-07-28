using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;

namespace Linkuity.Pipeline;

/// <summary>
/// Corpus-scale recall and precision audit. Unlike ScoringAuditService, which materializes every
/// candidate pair with a full per-field breakdown, this service is aggregate-only and allocates
/// no pair set: candidate pairs are owned by their lowest shared active key and emitted exactly
/// once. See docs/superpowers/specs/2026-07-28-missed-merge-detection-design.md.
/// </summary>
public sealed class CorpusAuditService
{
    private static readonly string[] SupportedScoring = ["weighted", "identifier-weighted"];

    /// <summary>Interned blocking-key index. RecordKeys rows are ascending — Task 4's
    /// lowest-shared-key ownership rule needs it for a linear intersection scan.</summary>
    internal sealed record KeyIndex(int[][] RecordKeys, int[] KeyCount, int[][] KeyMembers, string[] KeyNames);

    private static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;

    private readonly IStrategyRegistry _registry;

    public CorpusAuditService(IStrategyRegistry registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public CorpusAuditResult Audit(
        IReadOnlyList<EntityRecord> records,
        MatchingProfile profile,
        IReadOnlyDictionary<string, string> groundTruth,
        int? maxBlockSize = null,
        bool gateMode = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(groundTruth);

        Require(profile.NormalizationStrategy == "identity", "normalizationStrategy",
            profile.NormalizationStrategy, "identity");
        Require(profile.SimilarityStrategy == "field-weighted", "similarityStrategy",
            profile.SimilarityStrategy, "field-weighted");
        Require(SupportedScoring.Contains(profile.ScoringStrategy, StringComparer.Ordinal),
            "scoringStrategy", profile.ScoringStrategy, "weighted or identifier-weighted");
        Require(profile.DecisionStrategy == "threshold", "decisionStrategy",
            profile.DecisionStrategy, "threshold");
        Require(profile.ClusteringStrategy == "union-find", "clusteringStrategy",
            profile.ClusteringStrategy, "union-find");

        var duplicate = records
            .GroupBy(r => r.SourceRecordId, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Duplicate SourceRecordId in input: '{duplicate.Key}'.");

        throw new NotImplementedException("Passes 1-4 arrive in Tasks 3-8.");
    }

    internal static KeyIndex BuildIndex(
        IReadOnlyList<EntityRecord> records, MatchingProfile profile, IStrategyRegistry registry,
        CancellationToken ct = default)
    {
        var normalization = registry.Normalization[profile.NormalizationStrategy];
        var keyIds = new Dictionary<string, int>(KeyComparer);
        var keyNames = new List<string>();
        var members = new List<List<int>>();
        var recordKeys = new int[records.Count][];

        for (var i = 0; i < records.Count; i++)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();

            var normalized = normalization.Normalize(records[i], profile);
            var ids = new SortedSet<int>();
            foreach (var strategyName in profile.BlockingStrategies)
                foreach (var key in registry.Blocking[strategyName].GenerateKeys(normalized, profile))
                {
                    if (!keyIds.TryGetValue(key, out var id))
                    {
                        id = keyNames.Count;
                        keyIds[key] = id;
                        keyNames.Add(key);
                        members.Add([]);
                    }
                    ids.Add(id);
                }

            recordKeys[i] = [.. ids];
            foreach (var id in ids) members[id].Add(i);
        }

        var keyMembers = new int[members.Count][];
        var keyCount = new int[members.Count];
        for (var k = 0; k < members.Count; k++)
        {
            keyMembers[k] = [.. members[k]];
            keyCount[k] = members[k].Count;
        }
        return new KeyIndex(recordKeys, keyCount, keyMembers, [.. keyNames]);
    }

    /// <summary>Engine parity: blocking-linear counts key frequency over a corpus that EXCLUDES
    /// the query record, so a block of size S has per-query frequency S-1 and is suppressed iff
    /// S-1 > maxBlockSize (BlockingAuditService.cs:118).</summary>
    internal static bool[] SuppressedKeys(KeyIndex index, int? maxBlockSize)
    {
        var suppressed = new bool[index.KeyCount.Length];
        if (maxBlockSize is not { } max) return suppressed;
        for (var k = 0; k < index.KeyCount.Length; k++)
            suppressed[k] = index.KeyCount[k] - 1 > max;
        return suppressed;
    }

    /// <summary>
    /// Emits each candidate pair EXACTLY ONCE with no deduplication structure: a pair is owned
    /// by the lowest active key id both records carry. Returns the OCCURRENCE count — how many
    /// block-pair visits happened — which is the real work measure and is always >= the number
    /// of emitted pairs.
    /// </summary>
    internal static long ForEachCandidatePair(
        KeyIndex index, int? maxBlockSize, Action<int, int> onPair, CancellationToken ct = default)
    {
        var suppressed = SuppressedKeys(index, maxBlockSize);
        long occurrences = 0;

        for (var k = 0; k < index.KeyMembers.Length; k++)
        {
            if (suppressed[k]) continue;
            var ids = index.KeyMembers[k];
            if (ids.Length < 2) continue;
            ct.ThrowIfCancellationRequested();

            for (var i = 0; i < ids.Length; i++)
                for (var j = i + 1; j < ids.Length; j++)
                {
                    occurrences++;
                    var a = ids[i];
                    var b = ids[j];
                    if (LowestSharedActiveKey(index.RecordKeys[a], index.RecordKeys[b], suppressed) == k)
                        onPair(a < b ? a : b, a < b ? b : a);
                }
        }
        return occurrences;
    }

    /// <summary>Lowest key id present in both ascending arrays and not suppressed; -1 if none.</summary>
    private static int LowestSharedActiveKey(int[] left, int[] right, bool[] suppressed)
    {
        int i = 0, j = 0;
        while (i < left.Length && j < right.Length)
        {
            if (left[i] < right[j]) i++;
            else if (left[i] > right[j]) j++;
            else
            {
                if (!suppressed[left[i]]) return left[i];
                i++; j++;
            }
        }
        return -1;
    }

    private static void Require(bool ok, string setting, string actual, string expected)
    {
        if (!ok)
            throw new ArgumentException(
                $"Corpus audit requires {setting} '{expected}' (profile has '{actual}'): the audit " +
                "models only the blocking-linear batch path and must not silently report on a " +
                "configuration it does not implement.");
    }
}
