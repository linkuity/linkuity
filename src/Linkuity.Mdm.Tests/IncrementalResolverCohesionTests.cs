using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Clustering;
using Linkuity.Matching.Profiles;
using Linkuity.Mdm.Resolution;

namespace Linkuity.Mdm.Tests;

/// <summary>
/// Task 10: the merge policy is consulted BEFORE a cluster is created or replaced. A component
/// whose own comparisons contradict it more often than the profile's MinClusterCohesion tolerates
/// does not form — every member reverts to a singleton and a ClusterDissolutionEvent records the
/// numbers that refused it. MinClusterCohesion is set explicitly per test (never inherited from
/// MatchingProfile's own default) so the threshold under test is visible in the test itself.
/// </summary>
public class IncrementalResolverCohesionTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid SourceId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    // Same fixture shape as Task 9's ClusterCohesionCounterTests: A shares every token with B and
    // with C; B and C share only "Zeta" (jaccard 1/7, below review). The resulting 3-record
    // component has 3 comparisons inside it and 2 agreements (A-B, A-C auto; B-C rejected) — an
    // agreement rate of 0.667. A threshold of 0.70 rejects it; 0.50 accepts it, so the same records
    // exercise both sides of the policy.
    private const string AName = "Alpha Beta Gamma Zeta Delta Epsilon Theta";
    private const string BName = "Alpha Beta Gamma Zeta";
    private const string CName = "Delta Epsilon Theta Zeta";

    private static MatchingProfile Profile(double minClusterCohesion) => new()
    {
        ContentType = "organization",
        Fields =
        [
            new ProfileField
            {
                Name = "organization_name",
                SemanticType = SemanticFieldType.OrganizationName,
                Roles = FieldRole.Matchable | FieldRole.Blocking,
                SimilarityEvaluator = "canonical-jaccard",
                Weight = 4.0
            }
        ],
        NormalizationStrategy = "identity",
        BlockingStrategies = ["token"],
        CandidateRetrievalStrategy = "linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = "identifier-weighted",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 0.41,
        ReviewThreshold = 0.31,
        MinClusterCohesion = minClusterCohesion
    };

    // BlockingKeys are set explicitly and identically on every fixture record, so candidacy in the
    // resolver's "blocking-linear" retrieval never depends on what the token-blocking strategy
    // would have derived from the name — every record here is a candidate for every other, and
    // only the similarity score decides what happens next.
    private static EntityRecord Record(string id, string name, Guid batchId) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = ProjectId,
        SourceId = SourceId,
        IngestBatchId = batchId,
        SourceRecordId = id,
        Fields = new Dictionary<string, string> { ["organization_name"] = name },
        BlockingKeys = ["tok:shared"],
        CreatedAt = Now
    };

    private static (IncrementalIngestResult Result, MutationSet Mutations) Resolve(
        IReadOnlyList<EntityRecord> incoming, InMemoryResolutionContext context, Guid batchId, double minClusterCohesion)
    {
        var profile = Profile(minClusterCohesion);
        var request = new IncrementalIngestRequest(
            ProjectId, SourceId, batchId, incoming,
            AutoMatchThreshold: profile.AutoMatchThreshold, ReviewThreshold: profile.ReviewThreshold);
        var project = new Project { Id = ProjectId, Name = "MDM", ContentType = "organization", CreatedAt = Now };
        var (result, mutations) = new IncrementalResolver(
                MatchingDefaults.CreateEngine(), hasIndex: false, new CohesionClusterMergePolicy())
            .Resolve(request, project, profile, incoming, context, Now);
        context.ApplyMutations(mutations);
        return (result, mutations);
    }

    private static bool IsActiveMultiMember(Cluster cluster)
        => cluster.Status != "merged" && cluster.MemberEntityRecordIds.Count > 1;

    [Fact]
    public void AClusterItsOwnComparisonsContradict_DoesNotForm()
    {
        var batch = Guid.NewGuid();
        var a = Record("a", AName, batch);
        var b = Record("b", BName, batch);
        var c = Record("c", CName, batch);

        var (_, mutations) = Resolve([a, b, c], new InMemoryResolutionContext(), batch, minClusterCohesion: 0.70);

        // No 3-member cluster forms; every member reverts to its own singleton. There is nothing
        // to tombstone (this component never had a pre-existing cluster), so all three are fresh
        // active 1-member Cluster rows.
        Assert.DoesNotContain(mutations.ClustersToUpsert, IsActiveMultiMember);
        Assert.Equal(3, mutations.ClustersToUpsert.Count);
        Assert.All(mutations.ClustersToUpsert, cl => Assert.Single(cl.MemberEntityRecordIds));
        Assert.Single(mutations.DissolutionEventsToInsert);
    }

    [Fact]
    public void BelowTheThresholdItFails_AboveItTheSameRecordsMerge()
    {
        var batch1 = Guid.NewGuid();
        var a1 = Record("a", AName, batch1);
        var b1 = Record("b", BName, batch1);
        var c1 = Record("c", CName, batch1);
        var (_, rejected) = Resolve([a1, b1, c1], new InMemoryResolutionContext(), batch1, minClusterCohesion: 0.70);
        Assert.DoesNotContain(rejected.ClustersToUpsert, IsActiveMultiMember);
        Assert.Single(rejected.DissolutionEventsToInsert);

        var batch2 = Guid.NewGuid();
        var a2 = Record("a", AName, batch2);
        var b2 = Record("b", BName, batch2);
        var c2 = Record("c", CName, batch2);
        var (_, accepted) = Resolve([a2, b2, c2], new InMemoryResolutionContext(), batch2, minClusterCohesion: 0.50);
        var formed = Assert.Single(accepted.ClustersToUpsert, cl => cl.MemberEntityRecordIds.Count == 3);
        Assert.Equal(3, formed.ComparisonsInside);
        Assert.Equal(2, formed.AgreementsInside);
        Assert.Empty(accepted.DissolutionEventsToInsert);
    }

    [Fact]
    public void ADissolvedClusterEmitsAnEventCarryingTheNumbersThatRefusedIt()
    {
        var batch = Guid.NewGuid();
        var a = Record("a", AName, batch);
        var b = Record("b", BName, batch);
        var c = Record("c", CName, batch);

        var (_, mutations) = Resolve([a, b, c], new InMemoryResolutionContext(), batch, minClusterCohesion: 0.70);

        var evt = Assert.Single(mutations.DissolutionEventsToInsert);
        Assert.Equal(3, evt.ComparisonsInside);
        Assert.Equal(2, evt.AgreementsInside);
        Assert.Null(evt.PreviousClusterId);
        Assert.Equal(3, evt.MemberEntityRecordIds.Count);
        Assert.Equal(ProjectId, evt.ProjectId);
        Assert.Equal(batch, evt.IngestBatchId);
        Assert.Equal(nameof(ClusterMergeVerdict.RejectedForCohesion), evt.Reason);
    }

    [Fact]
    public void APreviouslyPublishedClusterIsRechecked_AndCanDissolve()
    {
        var context = new InMemoryResolutionContext();
        var batch1 = Guid.NewGuid();
        var a = Record("a", AName, batch1);
        var b = Record("b", BName, batch1);
        var (_, firstMutations) = Resolve([a, b], context, batch1, minClusterCohesion: 0.70);
        var published = Assert.Single(firstMutations.ClustersToUpsert, cl => cl.MemberEntityRecordIds.Count == 2);
        Assert.Empty(firstMutations.DissolutionEventsToInsert);

        // C arrives in a later ingest. Retroactive re-evaluation needs no separate code path:
        // {A,B,C} is re-materialized exactly like any other component, and this time it fails.
        var batch2 = Guid.NewGuid();
        var c = Record("c", CName, batch2);
        var (_, secondMutations) = Resolve([c], context, batch2, minClusterCohesion: 0.70);

        Assert.DoesNotContain(secondMutations.ClustersToUpsert, IsActiveMultiMember);
        var evt = Assert.Single(secondMutations.DissolutionEventsToInsert);
        Assert.Equal(published.Id, evt.PreviousClusterId);
        Assert.Equal(3, evt.ComparisonsInside);
        Assert.Equal(2, evt.AgreementsInside);
    }

    [Fact]
    public void DissolutionRetiresTheGoldenRecord()
    {
        var context = new InMemoryResolutionContext();
        var batch1 = Guid.NewGuid();
        var a = Record("a", AName, batch1);
        var b = Record("b", BName, batch1);
        var (_, firstMutations) = Resolve([a, b], context, batch1, minClusterCohesion: 0.70);
        var published = Assert.Single(firstMutations.ClustersToUpsert, cl => cl.MemberEntityRecordIds.Count == 2);
        Assert.Contains(context.GoldenRecords, g => g.ClusterId == published.Id);

        var batch2 = Guid.NewGuid();
        var c = Record("c", CName, batch2);
        var (_, secondMutations) = Resolve([c], context, batch2, minClusterCohesion: 0.70);

        Assert.Contains(published.Id, secondMutations.GoldenRecordClusterIdsToClear);
        Assert.DoesNotContain(context.GoldenRecords, g => g.ClusterId == published.Id);
    }

    [Fact]
    public void WithCohesionSatisfied_NothingAboutClusteringChanges()
    {
        // A 0.0 floor accepts regardless of AgreementRate: this is the pre-Task-10 behaviour,
        // preserved when the policy has nothing to object to.
        var batch = Guid.NewGuid();
        var a = Record("a", AName, batch);
        var b = Record("b", BName, batch);
        var c = Record("c", CName, batch);

        var (_, mutations) = Resolve([a, b, c], new InMemoryResolutionContext(), batch, minClusterCohesion: 0.0);

        var cluster = Assert.Single(mutations.ClustersToUpsert);
        Assert.Equal(3, cluster.MemberEntityRecordIds.Count);
        Assert.Equal(3, cluster.ComparisonsInside);
        Assert.Equal(2, cluster.AgreementsInside);
        Assert.Empty(mutations.DissolutionEventsToInsert);
    }

    [Fact]
    public void OneRecordBridgingTwoEstablishedClusters_DissolvesBoth()
    {
        // P/Q and R/S are each an identical-core pair (jaccard 0.6, auto) — two established,
        // previously published clusters. D shares the P/Q core plus P's own unique token ("Pone")
        // and the R/S core plus R's own unique token ("Rone"), but not Q's or S's unique token, so
        // D-P and D-R land auto (0.4286, bridging both clusters into one component) while D-Q and
        // D-S land below review (0.25). The combined component -- 6 comparisons, 4 agreements,
        // 0.667 -- fails a 0.70 threshold and dissolves both.
        const string PName = "Alpha Beta Gamma Pone";
        const string QName = "Alpha Beta Gamma Qone";
        const string RName = "Mu Nu Xi Rone";
        const string SName = "Mu Nu Xi Sone";
        const string DName = "Alpha Beta Pone Mu Nu Rone";

        var context = new InMemoryResolutionContext();
        var batch1 = Guid.NewGuid();
        var p = Record("p", PName, batch1);
        var q = Record("q", QName, batch1);
        var (_, m1) = Resolve([p, q], context, batch1, minClusterCohesion: 0.70);
        var cluster1 = Assert.Single(m1.ClustersToUpsert, cl => cl.MemberEntityRecordIds.Count == 2);

        var batch2 = Guid.NewGuid();
        var r = Record("r", RName, batch2);
        var s = Record("s", SName, batch2);
        var (_, m2) = Resolve([r, s], context, batch2, minClusterCohesion: 0.70);
        var cluster2 = Assert.Single(m2.ClustersToUpsert, cl => cl.MemberEntityRecordIds.Count == 2);

        var batch3 = Guid.NewGuid();
        var d = Record("d", DName, batch3);
        var (_, m3) = Resolve([d], context, batch3, minClusterCohesion: 0.70);

        Assert.DoesNotContain(m3.ClustersToUpsert, IsActiveMultiMember);
        Assert.Equal(2, m3.DissolutionEventsToInsert.Count);
        Assert.Contains(m3.DissolutionEventsToInsert, e => e.PreviousClusterId == cluster1.Id);
        Assert.Contains(m3.DissolutionEventsToInsert, e => e.PreviousClusterId == cluster2.Id);
        foreach (var evt in m3.DissolutionEventsToInsert)
        {
            Assert.Equal(6, evt.ComparisonsInside);
            Assert.Equal(4, evt.AgreementsInside);
            Assert.Equal(5, evt.MemberEntityRecordIds.Count);
        }
    }
}
