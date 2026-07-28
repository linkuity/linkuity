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

    private static void Require(bool ok, string setting, string actual, string expected)
    {
        if (!ok)
            throw new ArgumentException(
                $"Corpus audit requires {setting} '{expected}' (profile has '{actual}'): the audit " +
                "models only the blocking-linear batch path and must not silently report on a " +
                "configuration it does not implement.");
    }
}
