namespace Linkuity.Pipeline.Tests;

public class CorpusAuditClusterMetricTests
{
    [Fact]
    public void PerfectClustering_AllPairsTruePositive()
    {
        int[] predicted = [0, 0, 0, 1, 1];
        string?[] truth = ["x", "x", "x", "y", "y"];

        var (tp, pp, ap) = CorpusAuditService.ClusterPairCounts(predicted, truth);

        Assert.Equal(4, tp);   // C(3,2) + C(2,2)
        Assert.Equal(4, pp);
        Assert.Equal(4, ap);
    }

    [Fact]
    public void OverMerging_InflatesPredictedPositiveOnly()
    {
        int[] predicted = [0, 0, 0, 0, 0];
        string?[] truth = ["x", "x", "x", "y", "y"];

        var (tp, pp, ap) = CorpusAuditService.ClusterPairCounts(predicted, truth);

        Assert.Equal(4, tp);
        Assert.Equal(10, pp);  // C(5,2)
        Assert.Equal(4, ap);
    }

    [Fact]
    public void UnderMerging_ReducesTruePositive()
    {
        int[] predicted = [0, 0, 2, 1, 1];
        string?[] truth = ["x", "x", "x", "y", "y"];

        var (tp, pp, ap) = CorpusAuditService.ClusterPairCounts(predicted, truth);

        Assert.Equal(2, tp);
        Assert.Equal(2, pp);
        Assert.Equal(4, ap);
    }

    /// <summary>
    /// Spec §7: metrics are computed over the LABELLED PROJECTION. An unlabeled record sitting
    /// in a predicted cluster must not add predicted-positive pairs, or precision is penalized
    /// for truth we simply do not have. It may still connect labelled records transitively.
    /// </summary>
    [Fact]
    public void UnlabeledRecordsAreExcludedFromPredictedPositivesToo()
    {
        int[] predicted = [0, 0, 0];
        string?[] truth = ["x", "x", null];

        var (tp, pp, ap) = CorpusAuditService.ClusterPairCounts(predicted, truth);

        Assert.Equal(1, tp);
        Assert.Equal(1, pp);   // NOT 3 — only the two labelled members count
        Assert.Equal(1, ap);
    }

    /// <summary>
    /// The exclusion above does not depend on WHERE the unlabeled record sits: ClusterPairCounts
    /// reads a contingency table, so the null's index is irrelevant. This is a position variant of
    /// the previous test, nothing more — it does NOT exercise any connecting behaviour, which
    /// happens in CorpusAuditService's union-find and is covered end-to-end by
    /// CorpusAuditPipelineTests.UnlabeledBridgeClustersATruePairBlockingNeverProposed_WithoutInflatingPredictedPositives.
    /// </summary>
    [Fact]
    public void UnlabeledRecordsExcludedFromPredictedPositives_RegardlessOfPosition()
    {
        int[] predicted = [0, 0, 0];
        string?[] truth = ["x", null, "x"];

        var (tp, pp, ap) = CorpusAuditService.ClusterPairCounts(predicted, truth);

        Assert.Equal(1, tp);
        Assert.Equal(1, pp);
        Assert.Equal(1, ap);
    }

    /// <summary>A single predicted cluster of 100,000 records is 4,999,950,000 pairs, which
    /// overflows int. This fails loudly if any counter is narrowed.</summary>
    [Fact]
    public void LargeClusterDoesNotOverflow()
    {
        const int n = 100_000;
        var predicted = new int[n];
        var truth = new string?[n];
        for (var i = 0; i < n; i++) { predicted[i] = 0; truth[i] = "same"; }

        var (tp, pp, ap) = CorpusAuditService.ClusterPairCounts(predicted, truth);

        Assert.Equal(4_999_950_000L, tp);
        Assert.Equal(4_999_950_000L, pp);
        Assert.Equal(4_999_950_000L, ap);
    }
}
