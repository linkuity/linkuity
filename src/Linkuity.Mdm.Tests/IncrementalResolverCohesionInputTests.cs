using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Mdm.Resolution;

namespace Linkuity.Mdm.Tests;

/// <summary>
/// Cohesion needs the DENOMINATOR — every comparison made inside a cluster, not only the ones
/// that matched. These fix which comparisons are persisted and which are correctly discarded.
/// </summary>
public class IncrementalResolverCohesionInputTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid SourceId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    /// <summary>
    /// One canonical-jaccard name field. A = {Beta, Delta, Zeta} is the token union of B =
    /// {Beta, Zeta} and C = {Delta, Zeta}: jaccard(A,B) = jaccard(A,C) = 2/3 ≈ 0.667 (auto,
    /// >= AutoMatchThreshold 0.41), while jaccard(B,C) = 1/3 ≈ 0.333 sits in the review band
    /// (>= ReviewThreshold 0.31, < AutoMatchThreshold) rather than below it — a comparison
    /// that scores under ReviewThreshold is classified NoMatch and never becomes a
    /// ResolutionEdge at all (MatchBandClassifier), so it could never reach this loop.
    /// So A-B and A-C merge, B-C is compared and declined — and that declined comparison is
    /// the only thing that can ever reveal the cluster's contradiction.
    /// </summary>
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
        ReviewThreshold = 0.31
    };

    private static EntityRecord Record(string id, string name, Guid batchId) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = ProjectId,
        SourceId = SourceId,
        IngestBatchId = batchId,
        SourceRecordId = id,
        Fields = new Dictionary<string, string> { ["organization_name"] = name },
        BlockingKeys = ["tok:zeta"],   // set explicitly so all three are candidates for each other
        CreatedAt = Now
    };

    // Applies mutations back into the read-through context, mirroring FileMetadataStore's own
    // ApplyMutations (source :264). Without this, a second Resolve call on the same context
    // would never see what an earlier call produced, and "already present" would be
    // indistinguishable from "never ingested" — exactly the distinction Test 4 exercises.
    private static void ApplyMutations(InMemoryResolutionContext context, MutationSet mutations)
    {
        context.Records.AddRange(mutations.RecordsToInsert);
        foreach (var cluster in mutations.ClustersToUpsert)
        {
            context.Clusters.RemoveAll(c => c.Id == cluster.Id);
            context.Clusters.Add(cluster);
        }
        context.GoldenRecords.RemoveAll(g => mutations.GoldenRecordClusterIdsToClear.Contains(g.ClusterId));
        foreach (var golden in mutations.GoldenRecordsToUpsert)
        {
            context.GoldenRecords.RemoveAll(g => g.Id == golden.Id);
            context.GoldenRecords.Add(golden);
        }
        context.GoldenRecordVersions.AddRange(mutations.VersionsToInsert);
    }

    private static (IncrementalIngestResult Result, MutationSet Mutations) Resolve(
        IReadOnlyList<EntityRecord> incoming, InMemoryResolutionContext context, Guid batchId)
    {
        var profile = Profile();
        var request = new IncrementalIngestRequest(
            ProjectId, SourceId, batchId, incoming,
            AutoMatchThreshold: profile.AutoMatchThreshold, ReviewThreshold: profile.ReviewThreshold);
        var project = new Project { Id = ProjectId, Name = "MDM", ContentType = "organization", CreatedAt = Now };
        var (result, mutations) = new IncrementalResolver(MatchingDefaults.CreateEngine(), hasIndex: false)
            .Resolve(request, project, profile, incoming, context, Now);
        ApplyMutations(context, mutations);
        return (result, mutations);
    }

    [Fact]
    public void ARejectedComparisonInsideACluster_IsPersisted()
    {
        var batch = Guid.NewGuid();
        var a = Record("a", "Beta Delta Zeta", batch);
        var b = Record("b", "Beta Zeta", batch);
        var c = Record("c", "Delta Zeta", batch);

        var (_, mutations) = Resolve([a, b, c], new InMemoryResolutionContext(), batch);

        var bc = mutations.EdgesToInsert.SingleOrDefault(e =>
            (e.LeftEntityRecordId == b.Id && e.RightEntityRecordId == c.Id) ||
            (e.LeftEntityRecordId == c.Id && e.RightEntityRecordId == b.Id));

        Assert.NotNull(bc);
        Assert.NotEqual("auto", bc!.Decision);
    }

    [Fact]
    public void TheAutoEdgesAreStillPersistedAsAuto()
    {
        // Regression guard: widening the band filter must not change what an auto edge looks like.
        var batch = Guid.NewGuid();
        var a = Record("a", "Beta Delta Zeta", batch);
        var b = Record("b", "Beta Zeta", batch);

        var (_, mutations) = Resolve([a, b], new InMemoryResolutionContext(), batch);

        Assert.Equal("auto", Assert.Single(mutations.EdgesToInsert).Decision);
    }

    [Fact]
    public void ARejectedComparisonBetweenRecordsThatStaySeparate_IsNotPersisted()
    {
        // The whole storage argument. These two share a blocking key so they ARE compared, but
        // they do not merge and end in different clusters, so the rejection is irrelevant to
        // either. Keeping it would be 11,000,007 rows instead of 574,690.
        var batch = Guid.NewGuid();
        var b = Record("b", "Beta Zeta", batch);
        var c = Record("c", "Delta Zeta", batch);

        var (_, mutations) = Resolve([b, c], new InMemoryResolutionContext(), batch);

        Assert.Empty(mutations.EdgesToInsert);
    }

    [Fact]
    public void ComparisonsFromAnEarlierIngest_AreNotRewrittenByALaterOne()
    {
        // The existing loop only records edges touching an incoming record. Keep that: a
        // comparison among records already present was persisted when they arrived, and
        // re-persisting it would double-count the denominator on every subsequent ingest.
        var first = Guid.NewGuid();
        var context = new InMemoryResolutionContext();
        var a = Record("a", "Beta Delta Zeta", first);
        var b = Record("b", "Beta Zeta", first);
        Resolve([a, b], context, first);

        var second = Guid.NewGuid();
        var c = Record("c", "Delta Zeta", second);
        var (_, mutations) = Resolve([c], context, second);

        Assert.DoesNotContain(mutations.EdgesToInsert, e =>
            (e.LeftEntityRecordId == a.Id && e.RightEntityRecordId == b.Id) ||
            (e.LeftEntityRecordId == b.Id && e.RightEntityRecordId == a.Id));
    }
}
