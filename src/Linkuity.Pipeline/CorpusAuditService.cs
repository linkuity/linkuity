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

    private static void Require(bool ok, string setting, string actual, string expected)
    {
        if (!ok)
            throw new ArgumentException(
                $"Corpus audit requires {setting} '{expected}' (profile has '{actual}'): the audit " +
                "models only the blocking-linear batch path and must not silently report on a " +
                "configuration it does not implement.");
    }
}
