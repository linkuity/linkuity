using Linkuity.Core.Models;
using Linkuity.Matching;

namespace Linkuity.Pipeline.Tests;

public class ClusterShapeAuditTests
{
    private static ClusterShapeResult Run(
        IReadOnlyList<EntityRecord> records, IReadOnlyDictionary<string, string> truth)
        => new ClusterShapeAuditService(MatchingDefaults.CreateRegistry())
            .Audit(records, CorpusAuditFixtures.Profile(), truth);

    [Fact]
    public void CorrectAndOverMergedClusters_AreCountedSeparately()
    {
        // Two names that canonicalize identically merge; "Beacon" stays apart.
        var records = new List<EntityRecord>
        {
            CorpusAuditFixtures.Org("a1", "Acme Holdings Corp"),
            CorpusAuditFixtures.Org("a2", "Acme Holdings LLC"),
            CorpusAuditFixtures.Org("b1", "Beacon Systems Inc")
        };
        var truth = new Dictionary<string, string> { ["a1"] = "A", ["a2"] = "B", ["b1"] = "C" };

        var result = Run(records, truth);

        var mixed = result.Groups.Single(g => g.Verdict == ClusterVerdict.Mixed);
        Assert.Equal(1, mixed.Clusters);
        Assert.Equal(2, mixed.Records);
        Assert.Equal(1, result.Counts.MultiRecordClusters);
        Assert.Equal(1, result.Counts.Singletons);
    }

    [Fact]
    public void ACorrectCluster_IsLabeledCorrect()
    {
        var records = new List<EntityRecord>
        {
            CorpusAuditFixtures.Org("a1", "Acme Holdings Corp"),
            CorpusAuditFixtures.Org("a2", "Acme Holdings LLC")
        };
        var truth = new Dictionary<string, string> { ["a1"] = "A", ["a2"] = "A" };

        var result = Run(records, truth);

        var correct = result.Groups.Single(g => g.Verdict == ClusterVerdict.Correct);
        Assert.Equal(1, correct.Clusters);
        Assert.Equal(0, result.Groups.Single(g => g.Verdict == ClusterVerdict.Mixed).Clusters);
    }

    [Fact]
    public void AClusterWithNoLabeledMembers_IsUnlabeledNotCorrect()
    {
        // Silence must not read as success: an unlabeled cluster proves nothing either way.
        var records = new List<EntityRecord>
        {
            CorpusAuditFixtures.Org("x1", "Acme Holdings Corp"),
            CorpusAuditFixtures.Org("x2", "Acme Holdings LLC")
        };

        var result = Run(records, new Dictionary<string, string>());

        Assert.Equal(1, result.Groups.Single(g => g.Verdict == ClusterVerdict.Unlabeled).Clusters);
        Assert.Equal(0, result.Groups.Single(g => g.Verdict == ClusterVerdict.Correct).Clusters);
        Assert.Equal(2, result.Counts.UnlabeledRecords);
    }

    [Fact]
    public void ClusterOfTwo_IsNeverCountedAsSplitByOneEdge()
    {
        // Its only edge is trivially a bridge. Counting that as chaining would put a floor of
        // 100% under every pair in the corpus and make the statistic meaningless.
        var records = new List<EntityRecord>
        {
            CorpusAuditFixtures.Org("a1", "Acme Holdings Corp"),
            CorpusAuditFixtures.Org("a2", "Acme Holdings LLC")
        };
        var truth = new Dictionary<string, string> { ["a1"] = "A", ["a2"] = "A" };

        var result = Run(records, truth);

        var row = Assert.Single(result.LargestClusters);
        Assert.Equal(2, row.Size);
        Assert.False(row.SplitByOneEdge);
        Assert.Equal(0, result.Groups.Single(g => g.Verdict == ClusterVerdict.Correct).ClustersOfThreeOrMore);
    }

    [Fact]
    public void ComparedPairs_RecordsHowMuchOfTheClusterWasActuallyLookedAt()
    {
        // Three records that all share a blocking key: every pair inside is compared, so the
        // measurement should report full coverage rather than assuming it.
        var records = new List<EntityRecord>
        {
            CorpusAuditFixtures.Org("a1", "Acme Holdings Corp"),
            CorpusAuditFixtures.Org("a2", "Acme Holdings LLC"),
            CorpusAuditFixtures.Org("a3", "Acme Holdings Co")
        };
        var truth = new Dictionary<string, string> { ["a1"] = "A", ["a2"] = "A", ["a3"] = "A" };

        var result = Run(records, truth);

        var row = Assert.Single(result.LargestClusters);
        Assert.Equal(3, row.Size);
        Assert.Equal(3, row.PossiblePairs);
        Assert.Equal(3, row.ComparedPairs);
        Assert.Equal(1.0, row.ComparedFraction, 6);
        Assert.False(row.SplitByOneEdge);        // a triangle has no bridge
    }

    [Fact]
    public void SizeBands_OmitBandsWithNoClusters()
    {
        var records = new List<EntityRecord>
        {
            CorpusAuditFixtures.Org("a1", "Acme Holdings Corp"),
            CorpusAuditFixtures.Org("a2", "Acme Holdings LLC")
        };
        var truth = new Dictionary<string, string> { ["a1"] = "A", ["a2"] = "A" };

        var result = Run(records, truth);

        var band = Assert.Single(result.SizeBands);
        Assert.Equal("2", band.Band);
        Assert.Equal(ClusterVerdict.Correct, band.Verdict);
    }

    [Fact]
    public void UnsupportedProfile_IsRefusedRatherThanReportedOn()
    {
        var profile = CorpusAuditFixtures.Clone(CorpusAuditFixtures.Profile(), clusteringStrategy: "hierarchical");

        var ex = Assert.Throws<ArgumentException>(() =>
            new ClusterShapeAuditService(MatchingDefaults.CreateRegistry())
                .Audit([CorpusAuditFixtures.Org("a", "Acme")], profile, new Dictionary<string, string>()));

        Assert.Contains("clusteringStrategy", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Median_UsesALowerMedianSoTheValueBelongsToARealCluster()
    {
        Assert.Equal(2.0, ClusterShapeAuditService.Median([1.0, 2.0, 3.0, 4.0]));
        Assert.Equal(3.0, ClusterShapeAuditService.Median([1.0, 3.0, 5.0]));
        Assert.Equal(0.0, ClusterShapeAuditService.Median([]));
    }
}
