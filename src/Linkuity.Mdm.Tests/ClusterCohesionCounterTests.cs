using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Clustering;
using Linkuity.Matching.Profiles;
using Linkuity.Mdm.Resolution;

namespace Linkuity.Mdm.Tests;

/// <summary>
/// Cohesion is agreements over comparisons MADE INSIDE a cluster. These fix which comparisons
/// reach the counters — in particular that a confident rejection counts, which is the case the
/// engine's review-threshold filter used to hide.
/// </summary>
public class ClusterCohesionCounterTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid SourceId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    // A shares every token with B and with C. B and C share only "Zeta" — a jaccard of 1/7,
    // BELOW the review threshold. That pair is the one the old code path discarded entirely,
    // and it is exactly the kind of confident rejection cohesion depends on.
    private const string AName = "Alpha Beta Gamma Zeta Delta Epsilon Theta";
    private const string BName = "Alpha Beta Gamma Zeta";
    private const string CName = "Delta Epsilon Theta Zeta";

    // One canonical-jaccard name field, token-only names so the organization canonicalizer (which
    // strips leading articles and legal suffixes) has nothing to strip and the jaccard values
    // above land exactly where the arithmetic says they should.
    //
    // MinClusterCohesion = 0.5 (below every agreement rate this fixture produces — 1.0 and 2/3 —
    // so it never changes which clusters form) is required here, not incidental: [C1] gates
    // comparison capture and cohesion tallying on IClusterMergePolicy.CanReject(profile), which is
    // false for MinClusterCohesion/MaxAutoClusterSize both null. Counters read 0/0 while the
    // policy cannot reject anything under a profile, by design — see
    // CountersStayZeroWhenThePolicyCannotRejectAnything below for that case on its own.
    private static MatchingProfile Profile() => new()
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
        MinClusterCohesion = 0.5
    };

    // BlockingKeys are set explicitly and identically on every fixture record, so candidacy in
    // the resolver's "blocking-linear" retrieval never depends on what the token-blocking
    // strategy would have derived from the name — every record here is a candidate for every
    // other, and only the similarity score decides what happens next.
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
        IReadOnlyList<EntityRecord> incoming, InMemoryResolutionContext context, Guid batchId)
        => Resolve(incoming, context, batchId, Profile());

    private static (IncrementalIngestResult Result, MutationSet Mutations) Resolve(
        IReadOnlyList<EntityRecord> incoming, InMemoryResolutionContext context, Guid batchId, MatchingProfile profile)
    {
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

    [Fact]
    public void ARejectionBelowTheReviewThreshold_IsCounted()
    {
        // The regression this task exists for. Cluster {A,B,C} has three comparisons inside it;
        // two agreed (A-B, A-C auto). If the sub-review B-C comparison is invisible, the counters
        // read 2/2 and the cluster looks perfectly cohesive.
        var batch = Guid.NewGuid();
        var a = Record("a", AName, batch);
        var b = Record("b", BName, batch);
        var c = Record("c", CName, batch);

        var (_, mutations) = Resolve([a, b, c], new InMemoryResolutionContext(), batch);

        var cluster = Assert.Single(mutations.ClustersToUpsert);
        Assert.Equal(3, cluster.MemberEntityRecordIds.Count);
        Assert.Equal(3, cluster.ComparisonsInside);
        Assert.Equal(2, cluster.AgreementsInside);
    }

    [Fact]
    public void AComparisonWhoseEndpointsLandInDifferentClusters_IsNotCounted()
    {
        // B and C alone: they ARE compared (shared blocking key) but jaccard(B,C) = 1/7 is below
        // review threshold, so they never merge and land in two singleton clusters. Neither
        // cluster's counters move — the comparison says nothing about either.
        var batch = Guid.NewGuid();
        var b = Record("b", BName, batch);
        var c = Record("c", CName, batch);

        var (_, mutations) = Resolve([b, c], new InMemoryResolutionContext(), batch);

        Assert.Equal(2, mutations.ClustersToUpsert.Count);
        foreach (var cluster in mutations.ClustersToUpsert)
        {
            Assert.Equal(0, cluster.ComparisonsInside);
            Assert.Equal(0, cluster.AgreementsInside);
        }
    }

    [Fact]
    public void CountersAccumulateAcrossIngests()
    {
        // Ingest A and B, then C. After the second ingest the cluster's counters must include
        // the A-B comparison from the first run, or the rate resets every time.
        var context = new InMemoryResolutionContext();
        var first = Guid.NewGuid();
        var a = Record("a", AName, first);
        var b = Record("b", BName, first);
        Resolve([a, b], context, first);

        var second = Guid.NewGuid();
        var c = Record("c", CName, second);
        var (_, mutations) = Resolve([c], context, second);

        // Same three comparisons as the single-batch case (A-B, A-C, B-C), just spread over two
        // ingests: 3 comparisons total, 2 agreements (A-B and A-C auto; B-C rejected).
        var cluster = Assert.Single(mutations.ClustersToUpsert);
        Assert.Equal(3, cluster.MemberEntityRecordIds.Count);
        Assert.Equal(3, cluster.ComparisonsInside);
        Assert.Equal(2, cluster.AgreementsInside);
    }

    [Fact]
    public void MergingTwoClusters_SumsTheirCountersPlusThisRunsCrossComparisons()
    {
        // Two established clusters joined by an incoming record: the survivor's counters are
        // both clusters' counters plus the comparisons observed across them in this run.
        //
        // {P,Q} and {R,S} are each an identical-name pair (jaccard 1.0, auto) — one comparison,
        // one agreement per cluster. D shares half its tokens with each side (jaccard 0.5, auto)
        // and bridges both clusters into one component of 5, contributing four new auto
        // comparisons (D-P, D-Q, D-R, D-S) this run.
        const string PName = "Mu Nu Xi";
        const string QName = "Mu Nu Xi";
        const string RName = "Pi Rho Sigma";
        const string SName = "Pi Rho Sigma";
        const string DName = "Mu Nu Xi Pi Rho Sigma";

        var context = new InMemoryResolutionContext();
        var batch1 = Guid.NewGuid();
        var p = Record("p", PName, batch1);
        var q = Record("q", QName, batch1);
        Resolve([p, q], context, batch1);

        var batch2 = Guid.NewGuid();
        var r = Record("r", RName, batch2);
        var s = Record("s", SName, batch2);
        Resolve([r, s], context, batch2);

        var batch3 = Guid.NewGuid();
        var d = Record("d", DName, batch3);
        var (_, mutations) = Resolve([d], context, batch3);

        var merged = Assert.Single(mutations.ClustersToUpsert, c => c.MemberEntityRecordIds.Count == 5);
        Assert.Equal(6, merged.ComparisonsInside);  // 1 + 1 (base) + 4 (this run)
        Assert.Equal(6, merged.AgreementsInside);   // 1 + 1 (base) + 4 (this run), all auto
    }

    [Fact]
    public void ACounterIsNeverDecrementedByARecordThatDoesNotJoin()
    {
        // Guards double-counting in the other direction. A record that is compared against an
        // established cluster's members but does not join it (its endpoints land in different
        // clusters) must leave that cluster's stored counters exactly as they were.
        var context = new InMemoryResolutionContext();
        var first = Guid.NewGuid();
        var a = Record("a", AName, first);
        var b = Record("b", BName, first);
        var (_, firstMutations) = Resolve([a, b], context, first);
        var establishedClusterId = Assert.Single(firstMutations.ClustersToUpsert).Id;

        var second = Guid.NewGuid();
        var e = Record("e", "Omega Chi Psi", second);
        Resolve([e], context, second);

        var establishedCluster = context.Clusters.Single(c => c.Id == establishedClusterId);
        Assert.Equal(1, establishedCluster.ComparisonsInside);
        Assert.Equal(1, establishedCluster.AgreementsInside);
    }

    [Fact]
    public void CountersStayZeroWhenThePolicyCannotRejectAnything()
    {
        // [C1] MinClusterCohesion/MaxAutoClusterSize both null (the stage-1a shipped default) means
        // IClusterMergePolicy.CanReject(profile) is false, and IncrementalResolver now skips
        // comparison capture and cohesion tallying entirely rather than doing the bookkeeping for
        // counters nothing can ever read: same {A,B,C} comparisons as
        // ARejectionBelowTheReviewThreshold_IsCounted (3 compared, 2 agreed), but the cluster's
        // stored counters must read 0/0, not 3/2. This is a deliberate consequence, not a gap:
        // turning cohesion on for an already-ingested project is a re-ingest, not a live toggle.
        var off = Profile() with { MinClusterCohesion = null };
        var batch = Guid.NewGuid();
        var a = Record("a", AName, batch);
        var b = Record("b", BName, batch);
        var c = Record("c", CName, batch);

        var (_, mutations) = Resolve([a, b, c], new InMemoryResolutionContext(), batch, off);

        var cluster = Assert.Single(mutations.ClustersToUpsert);
        Assert.Equal(3, cluster.MemberEntityRecordIds.Count);
        Assert.Equal(0, cluster.ComparisonsInside);
        Assert.Equal(0, cluster.AgreementsInside);
    }
}
