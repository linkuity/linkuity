using Linkuity.Matching.Clustering;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Tests;

public class ClusterCohesionTests
{
    private static MatchingProfile Profile(double cohesion = 0.60, int? maxSize = null) => new()
    {
        ContentType = "organization",
        Fields = [],
        NormalizationStrategy = "identity",
        BlockingStrategies = ["token"],
        CandidateRetrievalStrategy = "linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = "evidence",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 8.0,
        ReviewThreshold = 4.0,
        MinClusterCohesion = cohesion,
        MaxAutoClusterSize = maxSize
    };

    private static readonly CohesionClusterMergePolicy Policy = new();

    [Fact]
    public void AgreementRateIsAgreementsOverComparisons()
    {
        var counts = new ClusterEvidenceCounts(Members: 10, ComparisonsInside: 40, AgreementsInside: 30);

        Assert.Equal(0.75, counts.AgreementRate, 12);
    }

    [Fact]
    public void AClusterWhoseComparisonsMostlyAgree_IsAccepted()
    {
        var verdict = Policy.Evaluate(new ClusterEvidenceCounts(10, 40, 30), Profile());

        Assert.Equal(ClusterMergeVerdict.Accepted, verdict);
    }

    [Fact]
    public void AClusterContradictedByItsOwnComparisons_IsRejected()
    {
        // The 29,477-record cluster's actual numbers: 299,323 pairs compared inside it, 162,258
        // judged to match. The engine said "different companies" ~137,000 times and was overruled.
        var verdict = Policy.Evaluate(new ClusterEvidenceCounts(29_477, 299_323, 162_258), Profile());

        Assert.Equal(ClusterMergeVerdict.RejectedForCohesion, verdict);
    }

    [Fact]
    public void ExactlyAtTheThreshold_IsAccepted()
    {
        var verdict = Policy.Evaluate(new ClusterEvidenceCounts(10, 100, 60), Profile(cohesion: 0.60));

        Assert.Equal(ClusterMergeVerdict.Accepted, verdict);
    }

    [Fact]
    public void APairIsAlwaysAccepted_BecauseItsOneComparisonIsTheMergeItself()
    {
        var verdict = Policy.Evaluate(new ClusterEvidenceCounts(2, 1, 1), Profile());

        Assert.Equal(ClusterMergeVerdict.Accepted, verdict);
    }

    [Fact]
    public void AClusterWithNoComparisonsInside_IsAccepted_NotRejectedAsZeroPercent()
    {
        // Divide-by-zero must not read as total disagreement. A cluster the engine never looked
        // inside has not contradicted itself; absence of evidence is not evidence of a defect.
        var verdict = Policy.Evaluate(new ClusterEvidenceCounts(5, 0, 0), Profile());

        Assert.Equal(ClusterMergeVerdict.Accepted, verdict);
    }

    [Fact]
    public void TheSizeGuardIsOffByDefault()
    {
        var verdict = Policy.Evaluate(new ClusterEvidenceCounts(1_000_000, 10, 10), Profile());

        Assert.Equal(ClusterMergeVerdict.Accepted, verdict);
    }

    [Fact]
    public void WhenTheSizeGuardIsSet_ALargerClusterIsRejected()
    {
        var verdict = Policy.Evaluate(new ClusterEvidenceCounts(21, 100, 100), Profile(maxSize: 20));

        Assert.Equal(ClusterMergeVerdict.RejectedForSize, verdict);
    }

    [Fact]
    public void AClusterExactlyAtTheSizeLimit_IsAccepted()
    {
        var verdict = Policy.Evaluate(new ClusterEvidenceCounts(20, 100, 100), Profile(maxSize: 20));

        Assert.Equal(ClusterMergeVerdict.Accepted, verdict);
    }

    [Fact]
    public void CohesionIsCheckedBeforeSize_SoTheReportedReasonIsTheRealOne()
    {
        var verdict = Policy.Evaluate(new ClusterEvidenceCounts(100, 100, 10), Profile(maxSize: 20));

        Assert.Equal(ClusterMergeVerdict.RejectedForCohesion, verdict);
    }
}
