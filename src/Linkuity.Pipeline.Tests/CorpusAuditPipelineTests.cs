using Linkuity.Core.Models;
using Linkuity.Matching;

namespace Linkuity.Pipeline.Tests;

/// <summary>
/// End-to-end tests for <see cref="CorpusAuditService.Audit"/> — the first point at which the
/// four passes (index, ownership, scoring/clustering, pair-counting) run as one instrument.
/// </summary>
public class CorpusAuditPipelineTests
{
    private static CorpusAuditResult Run(
        IReadOnlyList<EntityRecord> records, IReadOnlyDictionary<string, string> groundTruth)
        => new CorpusAuditService(MatchingDefaults.CreateRegistry())
            .Audit(records, CorpusAuditFixtures.Profile(), groundTruth);

    [Fact]
    public void DirectAutoTruePair_IsReachableScoredAndClustered()
    {
        var records = new[]
        {
            CorpusAuditFixtures.Org("a1", "ACME WIDGETS INC"),
            CorpusAuditFixtures.Org("a2", "Acme Widgets, Inc.")
        };
        var truth = new Dictionary<string, string> { ["a1"] = "e1", ["a2"] = "e1" };

        var result = Run(records, truth);

        Assert.Equal(2, result.Counts.Records);
        Assert.Equal(0, result.Counts.UnlabeledRecordCount);
        Assert.Equal(1, result.Counts.TruePairs);
        Assert.Equal(1, result.Counts.ReachableTruePairs);
        Assert.Equal(1, result.Counts.DirectAutoTruePairs);
        Assert.Equal(1, result.Counts.TruePositive);
        Assert.Equal(1, result.Counts.PredictedPositive);
        Assert.Equal(1, result.Counts.ActualPositive);
        Assert.Equal(1.0, result.Metrics.Reachability, precision: 6);
        Assert.Equal(1.0, result.Metrics.DirectAutoRecall, precision: 6);
        Assert.Equal(1.0, result.Metrics.PostClusterPairwiseRecall, precision: 6);
        Assert.Equal(1.0, result.Metrics.ClusterPairwisePrecision, precision: 6);

        // Inputs echo the profile so a report can never be read against the wrong configuration.
        Assert.Equal(50, result.Inputs.EffectiveMaxBlockSize);
        Assert.Equal(0.41, result.Inputs.AutoMatchThreshold, precision: 6);
        var coverage = Assert.Single(result.Inputs.FieldCoverage);
        Assert.Equal("organization_name", coverage.FieldName);
        Assert.Equal(4.0, coverage.Weight, precision: 6);
        Assert.Equal(1, coverage.PairsPopulatedBothSides);

        var outcome = Assert.Single(result.AllTruePairs);
        Assert.Equal("a1", outcome.LeftSourceRecordId);
        Assert.Equal("a2", outcome.RightSourceRecordId);
        Assert.Equal(Stratum.S1Identical, outcome.Stratum);
        Assert.True(outcome.Reachable);
        Assert.Equal(CorpusBand.Auto, outcome.Band);
        Assert.Equal(1.0, outcome.Score!.Value, precision: 6);
        Assert.True(outcome.SameCluster);

        var s1 = result.Strata.Single(s => s.Id == Stratum.S1Identical);
        Assert.Equal(1, s1.TruePairs);
        Assert.Equal(1, s1.Auto);
        Assert.Equal(1, s1.PostClusterTruePositive);
        Assert.Equal(1.0, s1.PostClusterPairwiseRecall!.Value, precision: 6);
        Assert.All(result.Strata.Where(s => s.Id != Stratum.S1Identical), s => Assert.Equal(0, s.TruePairs));

        Assert.Equal(1, result.ClusterSummary.GoldenRecordCount);
        Assert.Equal(2, result.ClusterSummary.LargestClusterSize);
        Assert.Equal(1, result.ClusterSummary.UnifiedClusterCount);
        Assert.Equal(0, result.ClusterSummary.SingletonCount);
    }

    /// <summary>
    /// Spec §7, tested end-to-end because it is only true end-to-end: the labelled projection is
    /// a property of union-find and ClusterPairCounts TOGETHER, and ClusterPairCounts alone cannot
    /// show it (feed it a root array and the connectivity has already happened off-stage).
    ///
    /// a1 and a3 share a ground-truth label but share NO blocking key, so the audit never proposes
    /// them as a candidate pair — Reachable is false and Band is null, the signature of a pair the
    /// blocking stage cannot see. The UNLABELLED bridge b1 overlaps both by half its tokens, so
    /// a1-b1 and b1-a3 both land in the auto band and union-find transitively merges all three.
    /// The test asserts BOTH halves of the requirement: the unreachable true pair still comes out
    /// a post-cluster true positive, AND the unlabelled bridge adds nothing to predicted positives
    /// (2 labelled members => C(2,2) = 1, not C(3,2) = 3), so precision is not charged for truth
    /// we do not have.
    /// </summary>
    [Fact]
    public void UnlabeledBridgeClustersATruePairBlockingNeverProposed_WithoutInflatingPredictedPositives()
    {
        var records = new[]
        {
            CorpusAuditFixtures.Org("a1", "ALPHA BETA GAMMA"),
            CorpusAuditFixtures.Org("b1", "ALPHA BETA GAMMA ZULU YANKEE XRAY"),
            CorpusAuditFixtures.Org("a3", "ZULU YANKEE XRAY")
        };
        var truth = new Dictionary<string, string> { ["a1"] = "e1", ["a3"] = "e1" };

        var result = Run(records, truth);

        Assert.Equal(1, result.Counts.UnlabeledRecordCount);

        // a1-a3 share no blocking key: only a1-b1 and b1-a3 are ever emitted.
        Assert.Equal(2, result.Counts.CandidatePairs);

        var outcome = Assert.Single(result.AllTruePairs);
        Assert.Equal("a1", outcome.LeftSourceRecordId);
        Assert.Equal("a3", outcome.RightSourceRecordId);
        Assert.False(outcome.Reachable);
        Assert.Null(outcome.Band);
        Assert.Null(outcome.Score);
        Assert.True(outcome.SameCluster);

        Assert.Equal(0, result.Counts.ReachableTruePairs);
        Assert.Equal(0, result.Counts.DirectAutoTruePairs);
        Assert.Equal(0.0, result.Metrics.DirectAutoRecall, precision: 6);

        // One cluster holding all three records...
        Assert.Equal(1, result.ClusterSummary.GoldenRecordCount);
        Assert.Equal(3, result.ClusterSummary.LargestClusterSize);

        // ...yet the labelled projection counts only the two labelled members.
        Assert.Equal(1, result.Counts.TruePositive);
        Assert.Equal(1, result.Counts.PredictedPositive);
        Assert.Equal(1, result.Counts.ActualPositive);
        Assert.Equal(1.0, result.Metrics.PostClusterPairwiseRecall, precision: 6);
        Assert.Equal(1.0, result.Metrics.ClusterPairwisePrecision, precision: 6);

        // The two pairs with an unlabeled endpoint are reported separately, not silently dropped.
        Assert.Equal(2, result.Counts.UnlabeledEndpointPairs);
    }
}
