using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Clustering;
using Linkuity.Matching.Profiles;

namespace Linkuity.Pipeline.Tests;

public class CorpusAuditCohesionTests
{
    /// <summary>
    /// Rejects on a basis that has nothing to do with MinClusterCohesion/MaxAutoClusterSize —
    /// standing in for a future policy so the audit's "can this policy reject anything" decision
    /// can be proven to ask the INJECTED policy rather than recompute CohesionClusterMergePolicy's
    /// own null-checks. Test-only: production has exactly one IClusterMergePolicy implementation.
    /// </summary>
    private sealed class AlwaysRejectMultiRecordPolicy : IClusterMergePolicy
    {
        public string Name => "always-reject-test-stub";
        public bool CanReject(MatchingProfile profile) => true;
        public ClusterMergeVerdict Evaluate(ClusterEvidenceCounts counts, MatchingProfile profile)
            => counts.Members > 1 ? ClusterMergeVerdict.RejectedForCohesion : ClusterMergeVerdict.Accepted;
    }

    /// <summary>
    /// The same three-record contradiction the resolver tests use: A merges with B and with C,
    /// B and C are compared and decline. Agreement 2/3.
    /// </summary>
    private static List<EntityRecord> Contradiction() =>
    [
        CorpusAuditFixtures.Org("a", "Alpha Beta Gamma Zeta Delta Epsilon Theta"),
        CorpusAuditFixtures.Org("b", "Alpha Beta Gamma Zeta"),
        CorpusAuditFixtures.Org("c", "Delta Epsilon Theta Zeta")
    ];

    private static readonly Dictionary<string, string> Truth =
        new() { ["a"] = "A", ["b"] = "B", ["c"] = "C" };

    private static CorpusAuditResult Audit(double cohesion)
    {
        var profile = CorpusAuditFixtures.Clone(CorpusAuditFixtures.Profile(), minClusterCohesion: cohesion);
        return new CorpusAuditService(MatchingDefaults.CreateRegistry()).Audit(Contradiction(), profile, Truth);
    }

    [Fact]
    public void AClusterCohesionWouldRefuse_DoesNotAppearInTheAuditsClusterMetrics()
    {
        // If production refuses a cluster the audit still forms, every precision number the audit
        // reports describes a system nobody runs.
        var result = Audit(cohesion: 0.70);

        Assert.Equal(0, result.ClusterSummary.UnifiedClusterCount);
        Assert.Equal(3, result.ClusterSummary.SingletonCount);
    }

    [Fact]
    public void TheSameRecordsBelowTheThreshold_StillCluster()
    {
        // Shows the refusal is the policy's doing rather than the audit losing the records.
        var result = Audit(cohesion: 0.60);

        Assert.Equal(1, result.ClusterSummary.UnifiedClusterCount);
    }

    [Fact]
    public void WithCohesionSatisfied_EveryReportedNumberIsUnchanged()
    {
        // Guards the frozen baseline. Two records that simply agree must produce exactly the
        // metrics they produced before the policy existed.
        var records = new List<EntityRecord>
        {
            CorpusAuditFixtures.Org("a1", "Acme Holdings Corp"),
            CorpusAuditFixtures.Org("a2", "Acme Holdings LLC")
        };
        var truth = new Dictionary<string, string> { ["a1"] = "A", ["a2"] = "A" };
        var profile = CorpusAuditFixtures.Clone(CorpusAuditFixtures.Profile(), minClusterCohesion: 0.60);

        var result = new CorpusAuditService(MatchingDefaults.CreateRegistry()).Audit(records, profile, truth);

        Assert.Equal(1, result.ClusterSummary.UnifiedClusterCount);
        Assert.Equal(1, result.Counts.DirectAutoTruePairs);
    }

    [Fact]
    public void APolicyThatCanRejectOnOtherGrounds_IsConsultedEvenWhenBothProfileGuardsAreNull()
    {
        // Regression for the bug the fix itself could have introduced: the skip-collection guard
        // must ask the injected policy, not recompute CohesionClusterMergePolicy's own null-check
        // inline. Profile() leaves MinClusterCohesion and MaxAutoClusterSize both null — the
        // condition that made CohesionClusterMergePolicy.CanReject false — yet this policy still
        // rejects, proving the audit consulted IT rather than short-circuiting on those two fields.
        var records = new List<EntityRecord>
        {
            CorpusAuditFixtures.Org("a1", "Acme Holdings Corp"),
            CorpusAuditFixtures.Org("a2", "Acme Holdings LLC")
        };
        var truth = new Dictionary<string, string> { ["a1"] = "A", ["a2"] = "A" };
        var profile = CorpusAuditFixtures.Profile();

        var result = new CorpusAuditService(MatchingDefaults.CreateRegistry(), new AlwaysRejectMultiRecordPolicy())
            .Audit(records, profile, truth);

        Assert.Equal(0, result.ClusterSummary.UnifiedClusterCount);
        Assert.Equal(2, result.ClusterSummary.SingletonCount);
    }
}
