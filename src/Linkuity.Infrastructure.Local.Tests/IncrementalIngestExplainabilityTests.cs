using Linkuity.Core.Models;
using Linkuity.Infrastructure.Local;

namespace Linkuity.Infrastructure.Local.Tests;

public class IncrementalIngestExplainabilityTests
{
    private static FileMetadataStore NewStore(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), "linkuity-explain-" + Guid.NewGuid().ToString("N") + ".json");
        return new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = path });
    }

    private static EntityRecord Record(Guid projectId, Guid sourceId, Guid batchId, string srid, Dictionary<string, string> fields, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        SourceId = sourceId,
        IngestBatchId = batchId,
        SourceRecordId = srid,
        Fields = fields,
        CreatedAt = at
    };

    private static async Task<(Guid ProjectId, Guid SourceId)> SeedAsync(FileMetadataStore store, Dictionary<string, string> existing)
    {
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("MDM", "person", null, now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now, CancellationToken.None);
        var rec = Record(project.Id, source.Id, batch.Id, "existing-1", existing, now);
        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata(
                [rec], [],
                [new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [rec.Id], CreatedAt = now }],
                [], []),
            CancellationToken.None);
        return (project.Id, source.Id);
    }

    [Fact]
    public async Task AutoMatchEdge_PersistsDecisionAndBreakdown()
    {
        var store = NewStore(out _);
        var (projectId, sourceId) = await SeedAsync(store, new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "alice@example.com", ["last_name"] = "Smith" });
        var batch = await store.CreateIngestBatchAsync(projectId, sourceId, null, 1, DateTimeOffset.UtcNow, CancellationToken.None);
        var incoming = Record(projectId, sourceId, batch.Id, "in-1",
            new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "alice@example.com", ["last_name"] = "Smith" }, DateTimeOffset.UtcNow);

        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(projectId, sourceId, batch.Id, [incoming], 0.90, 0.75), CancellationToken.None);

        var edges = await store.ListMatchEdgesAsync(projectId, CancellationToken.None);
        var edge = Assert.Single(edges);
        Assert.Equal("auto", edge.Decision);
        Assert.NotEmpty(edge.Breakdown);
        Assert.Contains(edge.Breakdown, f => f.Signal.Length > 0);
    }

    [Fact]
    public async Task ReviewTask_PersistsBreakdown()
    {
        var store = NewStore(out _);
        // Matching last name plus a real, non-identifier first-name similarity signal (no email on
        // either side): last_name exact (2.0) + first_name "Robert"/"Bob" fuzzy 0.67 (1.0) over
        // total weight 3.0 = 0.89, which legitimately clears the review-floor gate — a genuinely
        // uncertain pair, not a blocking-key-only match.
        var (projectId, sourceId) = await SeedAsync(store, new Dictionary<string, string> { ["source"] = "CRM", ["first_name"] = "Robert", ["last_name"] = "Smith" });
        var batch = await store.CreateIngestBatchAsync(projectId, sourceId, null, 1, DateTimeOffset.UtcNow, CancellationToken.None);
        var incoming = Record(projectId, sourceId, batch.Id, "in-1",
            new Dictionary<string, string> { ["source"] = "CRM", ["first_name"] = "Bob", ["last_name"] = "Smith" }, DateTimeOffset.UtcNow);

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(projectId, sourceId, batch.Id, [incoming], 0.90, 0.75), CancellationToken.None);

        Assert.True(result.ReviewTasks >= 1);
        var tasks = await store.ListReviewTasksAsync(projectId, CancellationToken.None);
        Assert.All(tasks, t => Assert.NotEmpty(t.Breakdown));
    }
}
