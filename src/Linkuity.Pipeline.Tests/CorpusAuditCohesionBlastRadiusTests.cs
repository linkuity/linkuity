using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Clustering;

namespace Linkuity.Pipeline.Tests;

/// <summary>
/// Spec §6.4/§9.7: cohesion rejection is reject-wholesale, so a component that fails dissolves to
/// singletons IN FULL, taking any correct sub-grouping down with it. <see cref="CohesionBlastRadius"/>
/// answers "what did it destroy" — how many rejected components contained a true entity's ENTIRE
/// membership, and how many such correct clusters were lost. All expected numbers below are
/// hand-computed, not asserted against whatever the code currently produces.
/// </summary>
public class CorpusAuditCohesionBlastRadiusTests
{
    // ---- CorpusAuditService.ComputeBlastRadius: pure unit tests, no scoring involved ----

    private static readonly string?[] Labels = ["A", "A", "B", "B", "C"];

    private static readonly Dictionary<string, List<int>> ByLabel = new(StringComparer.Ordinal)
    {
        ["A"] = [0, 1],  // a two-member true entity
        ["B"] = [2, 3],  // a second two-member true entity
        ["C"] = [4]      // a singleton true entity: nothing for dissolution to lose
    };

    [Fact]
    public void ComponentFullyContainingATwoMemberTrueEntity_CountsAsOneClusterLost()
    {
        // Members 0 and 1 are ALL of A's members, plus one member of B (2) that is not B's full
        // extent (B also has member 3, absent here). Only A is a complete capture.
        var rejected = new List<CorpusAuditService.RejectedComponent>
        {
            new([0, 1, 2], ClusterMergeVerdict.RejectedForCohesion)
        };

        var result = CorpusAuditService.ComputeBlastRadius(rejected, Labels, ByLabel);

        Assert.Equal(1, result.RejectedComponents);
        Assert.Equal(1, result.ComponentsContainingALostCorrectCluster);
        Assert.Equal(1, result.CorrectClustersLost);
        Assert.Equal(2, result.RecordsInLostCorrectClusters);
    }

    [Fact]
    public void TwoDifferentComponents_EachFullyContainingADifferentTrueEntity_BothCounted()
    {
        var rejected = new List<CorpusAuditService.RejectedComponent>
        {
            new([0, 1, 4], ClusterMergeVerdict.RejectedForCohesion),   // captures A whole
            new([2, 3], ClusterMergeVerdict.RejectedForCohesion)       // captures B whole
        };

        var result = CorpusAuditService.ComputeBlastRadius(rejected, Labels, ByLabel);

        Assert.Equal(2, result.RejectedComponents);
        Assert.Equal(2, result.ComponentsContainingALostCorrectCluster);
        Assert.Equal(2, result.CorrectClustersLost);
        Assert.Equal(4, result.RecordsInLostCorrectClusters);
    }

    [Fact]
    public void PartialCapture_OfEveryTrueEntity_LosesNothing()
    {
        // One member from A, one from B: neither true entity's full membership is inside.
        var rejected = new List<CorpusAuditService.RejectedComponent>
        {
            new([0, 2], ClusterMergeVerdict.RejectedForCohesion)
        };

        var result = CorpusAuditService.ComputeBlastRadius(rejected, Labels, ByLabel);

        Assert.Equal(1, result.RejectedComponents);
        Assert.Equal(0, result.ComponentsContainingALostCorrectCluster);
        Assert.Equal(0, result.CorrectClustersLost);
        Assert.Equal(0, result.RecordsInLostCorrectClusters);
    }

    [Fact]
    public void SingletonTrueEntity_InsideARejectedComponent_IsNeverCountedAsLost()
    {
        // Member 4 (entity C) has no partner corpuswide, so dissolving its component to
        // singletons changes nothing for it — there is no cluster to lose.
        var rejected = new List<CorpusAuditService.RejectedComponent>
        {
            new([4, 0], ClusterMergeVerdict.RejectedForCohesion) // A only partially present too
        };

        var result = CorpusAuditService.ComputeBlastRadius(rejected, Labels, ByLabel);

        Assert.Equal(0, result.ComponentsContainingALostCorrectCluster);
        Assert.Equal(0, result.CorrectClustersLost);
    }

    [Fact]
    public void RejectedForSize_IsExcludedEntirely_EvenWhenItFullyContainsATrueEntity()
    {
        // This is a cohesion measurement (spec item 2): MaxAutoClusterSize rejections must not be
        // silently folded into the cohesion blast radius, even though the containment logic is
        // identical and would otherwise report a "loss".
        var rejected = new List<CorpusAuditService.RejectedComponent>
        {
            new([0, 1, 2, 3], ClusterMergeVerdict.RejectedForSize)
        };

        var result = CorpusAuditService.ComputeBlastRadius(rejected, Labels, ByLabel);

        Assert.Equal(0, result.RejectedComponents);
        Assert.Equal(0, result.ComponentsContainingALostCorrectCluster);
        Assert.Equal(0, result.CorrectClustersLost);
        Assert.Equal(0, result.RecordsInLostCorrectClusters);
    }

    [Fact]
    public void NoRejectedComponents_ReportsAllZero()
    {
        var result = CorpusAuditService.ComputeBlastRadius([], Labels, ByLabel);

        Assert.Equal(0, result.RejectedComponents);
        Assert.Equal(0, result.ComponentsContainingALostCorrectCluster);
        Assert.Equal(0, result.CorrectClustersLost);
        Assert.Equal(0, result.RecordsInLostCorrectClusters);
    }

    // ---- End-to-end through CorpusAuditService.Audit: proves the wiring, not just the math ----

    /// <summary>
    /// Six organization-name records, every one carrying the token "Zeta" so every one of the 15
    /// pairs is a candidate (token blocking). Two independent, mutually-correct true entities —
    /// AB = {a,b} and P = {p1,p2} — sit at opposite ends of one chain that a bridge record ("br")
    /// pulls together: a-b, a-c, b-br, br-p2 and p1-p2 all score canonical-jaccard >= 0.571 (above
    /// Profile()'s 0.41 auto threshold) and union; every other pair overlaps only on "Zeta" (or, for
    /// a-br and br-p1, tops out at 0.40 — just short of auto). Hand-computed jaccard for every pair:
    /// <code>
    /// a-b 4/7=.571 AUTO   a-c 4/7=.571 AUTO   a-br 4/10=.40   a-p1 1/13=.08   a-p2 1/10=.10
    /// b-c 1/7=.14         b-br 4/7=.571 AUTO  b-p1 1/10=.10   b-p2 1/7=.14
    /// c-br 1/10=.10       c-p1 1/10=.10       c-p2 1/7=.14
    /// br-p1 4/10=.40      br-p2 4/7=.571 AUTO
    /// p1-p2 4/7=.571 AUTO
    /// </code>
    /// 5 auto edges out of all 15 candidate pairs once the chain unions the 6 records into ONE
    /// final component: agreement rate 5/15 = 0.333, below every threshold this file exercises.
    /// AB's full two-record membership and P's full two-record membership are both entirely inside
    /// that one component — two correct clusters nested in the over-merge, both destroyed by
    /// reject-wholesale.
    /// </summary>
    private static List<EntityRecord> ChainedContradictionWithTwoCapturedTrueEntities() =>
    [
        CorpusAuditFixtures.Org("a", "Zeta W1 W2 W3 W4 W5 W6"),
        CorpusAuditFixtures.Org("b", "Zeta W1 W2 W3"),
        CorpusAuditFixtures.Org("c", "Zeta W4 W5 W6"),
        CorpusAuditFixtures.Org("br", "Zeta W1 W2 W3 X1 X2 X3"),
        CorpusAuditFixtures.Org("p1", "Zeta X1 X2 X3 X4 X5 X6"),
        CorpusAuditFixtures.Org("p2", "Zeta X1 X2 X3")
    ];

    private static readonly Dictionary<string, string> ChainedTruth = new()
    {
        ["a"] = "AB", ["b"] = "AB",       // fully captured — a genuinely correct 2-record cluster
        ["c"] = "C", ["br"] = "BR",       // singleton true entities: nothing to lose
        ["p1"] = "P", ["p2"] = "P"        // fully captured — a second genuinely correct cluster
    };

    [Fact]
    public void CohesionOff_BlastRadiusIsNull()
    {
        var result = new CorpusAuditService(MatchingDefaults.CreateRegistry())
            .Audit(ChainedContradictionWithTwoCapturedTrueEntities(), CorpusAuditFixtures.Profile(), ChainedTruth);

        Assert.Null(result.BlastRadius);
    }

    [Fact]
    public void CohesionOnAndLenient_RejectsNothing_BlastRadiusIsAllZero_NotNull()
    {
        // Cohesion is ACTIVE (so blast radius is a real measurement, not "off") but set low enough
        // that the 5/15 = 0.333 agreement rate above still clears it — nothing is rejected. Zero
        // must be reported explicitly, distinct from cohesion being off altogether.
        var profile = CorpusAuditFixtures.Clone(CorpusAuditFixtures.Profile(), minClusterCohesion: 0.10);

        var result = new CorpusAuditService(MatchingDefaults.CreateRegistry())
            .Audit(ChainedContradictionWithTwoCapturedTrueEntities(), profile, ChainedTruth);

        Assert.NotNull(result.BlastRadius);
        Assert.Equal(0, result.BlastRadius!.RejectedComponents);
        Assert.Equal(0, result.BlastRadius.ComponentsContainingALostCorrectCluster);
        Assert.Equal(0, result.BlastRadius.CorrectClustersLost);
        // The 5 auto edges (a-b, a-c, b-br, br-p2, p1-p2) still stand: one unified 6-record cluster.
        Assert.Equal(1, result.ClusterSummary.UnifiedClusterCount);
        Assert.Equal(0, result.ClusterSummary.SingletonCount);
    }

    [Fact]
    public void CohesionAtStage1bThreshold_RejectsTheChain_AndReportsBothCapturedTrueEntitiesAsLost()
    {
        var profile = CorpusAuditFixtures.Clone(CorpusAuditFixtures.Profile(), minClusterCohesion: 0.50);

        var result = new CorpusAuditService(MatchingDefaults.CreateRegistry())
            .Audit(ChainedContradictionWithTwoCapturedTrueEntities(), profile, ChainedTruth);

        // The whole 6-record chain forms one component pre-cohesion (agreement 0.333 < 0.50) and is
        // rejected wholesale, so every record reverts to a singleton.
        Assert.Equal(0, result.ClusterSummary.UnifiedClusterCount);
        Assert.Equal(6, result.ClusterSummary.SingletonCount);

        Assert.NotNull(result.BlastRadius);
        Assert.Equal(1, result.BlastRadius!.RejectedComponents);
        Assert.Equal(1, result.BlastRadius.ComponentsContainingALostCorrectCluster);
        Assert.Equal(2, result.BlastRadius.CorrectClustersLost);
        Assert.Equal(4, result.BlastRadius.RecordsInLostCorrectClusters);
    }
}
