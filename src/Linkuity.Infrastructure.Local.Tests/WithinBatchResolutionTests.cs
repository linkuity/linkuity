using Linkuity.Core.Models;
using Linkuity.Infrastructure.Local;

namespace Linkuity.Infrastructure.Local.Tests;

public class WithinBatchResolutionTests
{
    private static FileMetadataStore NewStore()
        => new(new FileMetadataStoreOptions { DatabasePath = Path.Combine(Path.GetTempPath(), "linkuity-wbr-" + Guid.NewGuid().ToString("N") + ".json") });

    private static EntityRecord Record(Guid projectId, Guid sourceId, Guid batchId, string srid, Dictionary<string, string> fields, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(), ProjectId = projectId, SourceId = sourceId, IngestBatchId = batchId,
        SourceRecordId = srid, Fields = fields, CreatedAt = at
    };

    private static async Task<(Guid ProjectId, Guid SourceId)> EmptyProjectAsync(FileMetadataStore store)
    {
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        return (project.Id, source.Id);
    }

    [Fact]
    public async Task TwoNetNewInBatchDuplicates_AutoBand_FormOneCluster()
    {
        var store = NewStore();
        var (projectId, sourceId) = await EmptyProjectAsync(store);
        var batch = await store.CreateIngestBatchAsync(projectId, sourceId, null, 2, DateTimeOffset.UtcNow, CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var a = Record(projectId, sourceId, batch.Id, "in-a", new() { ["source"] = "CRM", ["email"] = "maria@x.com", ["name"] = "Maria Garcia" }, now);
        var b = Record(projectId, sourceId, batch.Id, "in-b", new() { ["source"] = "CRM", ["email"] = "maria@x.com", ["name"] = "Maria Garcia" }, now);

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(projectId, sourceId, batch.Id, [a, b], 0.90, 0.75), CancellationToken.None);

        var clusters = await store.ListClustersAsync(projectId, CancellationToken.None);
        Assert.Single(clusters);
        Assert.Equal(2, clusters[0].MemberEntityRecordIds.Count);
        Assert.True(result.AutoMatches >= 1);
    }

    [Fact]
    public async Task TwoNetNewInBatchPair_ReviewBand_StaysSeparateWithReview()
    {
        var store = NewStore();
        var (projectId, sourceId) = await EmptyProjectAsync(store);
        var batch = await store.CreateIngestBatchAsync(projectId, sourceId, null, 2, DateTimeOffset.UtcNow, CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        // Similar names, no shared identifier -> review band, not auto.
        var a = Record(projectId, sourceId, batch.Id, "in-a", new() { ["source"] = "CRM", ["name"] = "Jonathan Smith", ["city"] = "Denver" }, now);
        var b = Record(projectId, sourceId, batch.Id, "in-b", new() { ["source"] = "CRM", ["name"] = "Jon Smith", ["city"] = "Denver" }, now);

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(projectId, sourceId, batch.Id, [a, b], 0.90, 0.75), CancellationToken.None);

        Assert.Equal(2, (await store.ListClustersAsync(projectId, CancellationToken.None)).Count);
        Assert.True(result.ReviewTasks >= 1);
    }

    private static async Task<(Guid ProjectId, Guid SourceId, EntityRecord R1, EntityRecord R2)> TwoSeparateClustersAsync(FileMetadataStore store)
    {
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 2, now, CancellationToken.None);
        var r1 = Record(project.Id, source.Id, batch.Id, "ex-1", new() { ["source"] = "CRM", ["email"] = "jon@acme.com", ["name"] = "Jonathan Smith" }, now);
        var r2 = Record(project.Id, source.Id, batch.Id, "ex-2", new() { ["source"] = "CRM", ["phone"] = "555-9876", ["name"] = "J Smith" }, now);
        await store.SaveCompletedBatchAsync(new CompletedBatchMetadata(
            [r1, r2], [],
            [
                new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [r1.Id], CreatedAt = now },
                new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [r2.Id], CreatedAt = now.AddSeconds(1) }
            ], [], []), CancellationToken.None);
        return (project.Id, source.Id, r1, r2);
    }

    // Weak-bridge fixture: C1 = email-bearing (Alice Adams), C2 = name-only (Jon Smith).
    // C1↔C2 share nothing → no edge between them; they seed as two separate clusters.
    private static async Task<(Guid ProjectId, Guid SourceId, EntityRecord R1, EntityRecord R2)> TwoSeparateClustersWeakBridgeAsync(FileMetadataStore store)
    {
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 2, now, CancellationToken.None);
        // C1: has email so X can auto-join via identifier floor; distinct name so C1↔C2 produce no edge.
        var r1 = Record(project.Id, source.Id, batch.Id, "wb-1",
            new() { ["source"] = "CRM", ["email"] = "jon@acme.com", ["name"] = "Alice Adams" }, now);
        // C2: name-only, no shared identifier with anyone else → can only be review-band linked.
        var r2 = Record(project.Id, source.Id, batch.Id, "wb-2",
            new() { ["source"] = "CRM", ["name"] = "Jon Smith" }, now);
        await store.SaveCompletedBatchAsync(new CompletedBatchMetadata(
            [r1, r2], [],
            [
                new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [r1.Id], CreatedAt = now },
                new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [r2.Id], CreatedAt = now.AddSeconds(1) }
            ], [], []), CancellationToken.None);
        return (project.Id, source.Id, r1, r2);
    }

    [Fact]
    public async Task WeakBridge_DoesNotMerge_CreatesClusterMergeSuggestion()
    {
        // X shares email with C1 (auto >= 0.90) and is name-similar to C2 "Jon Smith" (review [0.75, 0.90)).
        // Expected: clusters do NOT merge; exactly one cluster_merge_suggestion review task.
        var store = NewStore();
        var (projectId, sourceId, _, r2) = await TwoSeparateClustersWeakBridgeAsync(store);
        var batch = await store.CreateIngestBatchAsync(projectId, sourceId, null, 1, DateTimeOffset.UtcNow, CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var x = Record(projectId, sourceId, batch.Id, "in-x",
            new() { ["source"] = "CRM", ["email"] = "jon@acme.com", ["name"] = "Jonathan Smith" }, now);

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(projectId, sourceId, batch.Id, [x], 0.90, 0.75), CancellationToken.None);

        // Pinned counts: exactly 1 auto-match (X→C1 via email) and exactly 1 review task (X↔C2 name-similarity).
        Assert.Equal(1, result.AutoMatches);
        Assert.Equal(1, result.ReviewTasks);

        // Acceptance assertions.
        Assert.Equal(2, (await store.ListClustersAsync(projectId, CancellationToken.None)).Count); // no merge
        Assert.Empty(await store.ListClusterMergeEventsAsync(projectId, CancellationToken.None));
        var reviews = await store.ListReviewTasksAsync(projectId, CancellationToken.None);
        var suggestion = Assert.Single(reviews, t => t.Reason == "cluster_merge_suggestion");
        Assert.NotNull(suggestion.LeftClusterId);
        Assert.NotNull(suggestion.RightClusterId);
        Assert.NotEqual(suggestion.LeftClusterId, suggestion.RightClusterId);
        Assert.Equal(x.Id, suggestion.NewEntityRecordId);
    }

    [Theory]
    [InlineData(0, 1, 2)]
    [InlineData(2, 1, 0)]
    [InlineData(1, 2, 0)]
    public async Task Resolution_IsOrderIndependent(int i0, int i1, int i2)
    {
        // a~b auto (shared email), b~c auto (shared phone), a~c not directly -> transitive closure to one cluster.
        var store = NewStore();
        var (projectId, sourceId) = await EmptyProjectAsync(store);
        var batch = await store.CreateIngestBatchAsync(projectId, sourceId, null, 3, DateTimeOffset.UtcNow, CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var a = Record(projectId, sourceId, batch.Id, "a", new() { ["source"] = "CRM", ["email"] = "p@x.com", ["name"] = "Pat Lee" }, now);
        var b = Record(projectId, sourceId, batch.Id, "b", new() { ["source"] = "CRM", ["email"] = "p@x.com", ["phone"] = "555-1", ["name"] = "Pat Lee" }, now);
        var c = Record(projectId, sourceId, batch.Id, "c", new() { ["source"] = "CRM", ["phone"] = "555-1", ["name"] = "Patrick Lee" }, now);
        var ordered = new[] { a, b, c };

        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(projectId, sourceId, batch.Id, [ordered[i0], ordered[i1], ordered[i2]], 0.90, 0.75), CancellationToken.None);

        var clusters = await store.ListClustersAsync(projectId, CancellationToken.None);
        Assert.Single(clusters);                                  // transitive closure: a, b, c in one cluster
        Assert.Equal(3, clusters[0].MemberEntityRecordIds.Count);
    }

    [Fact]
    public async Task AutoBridge_MergesClusters_WithSurvivorTombstoneAndMergeEvent()
    {
        var store = NewStore();
        var (projectId, sourceId, r1, r2) = await TwoSeparateClustersAsync(store);
        var batch = await store.CreateIngestBatchAsync(projectId, sourceId, null, 1, DateTimeOffset.UtcNow, CancellationToken.None);
        // X shares email with C1 and phone with C2 -> auto into both (identifier flooring).
        var x = Record(projectId, sourceId, batch.Id, "in-x",
            new() { ["source"] = "CRM", ["email"] = "jon@acme.com", ["phone"] = "555-9876", ["name"] = "Jonathan Smith" }, DateTimeOffset.UtcNow);

        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(projectId, sourceId, batch.Id, [x], 0.90, 0.75), CancellationToken.None);

        var active = await store.ListClustersAsync(projectId, CancellationToken.None);
        Assert.Single(active);                                   // C1 and C2 merged into one survivor
        Assert.Equal(3, active[0].MemberEntityRecordIds.Count);  // r1, r2, x
        var merges = await store.ListClusterMergeEventsAsync(projectId, CancellationToken.None);
        Assert.Single(merges);
        Assert.Equal(active[0].Id, merges[0].SurvivorClusterId);
        Assert.Contains(x.Id, merges[0].TriggerRecordIds);
        Assert.Contains(r2.Id, merges[0].AbsorbedMemberEntityRecordIds); // loser's original member retained for lineage
        Assert.Single(await store.ListGoldenRecordsAsync(projectId, CancellationToken.None)); // one active golden record
    }
}
