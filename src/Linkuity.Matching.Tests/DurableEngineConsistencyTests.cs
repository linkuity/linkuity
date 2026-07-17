using Linkuity.Core.Models;
using Linkuity.Infrastructure.Local;

namespace Linkuity.Matching.Tests;

public class DurableEngineConsistencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "linkuity-m16-consistency-" + Guid.NewGuid().ToString("N"));

    public DurableEngineConsistencyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private FileMetadataStore NewStore(string name)
        => new(new FileMetadataStoreOptions { DatabasePath = Path.Combine(_root, name) });

    private static EntityRecord Record(Guid p, Guid s, Guid b, string srid, Dictionary<string, string> fields, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(), ProjectId = p, SourceId = s, IngestBatchId = b, SourceRecordId = srid, Fields = fields, CreatedAt = at
    };

    private async Task<(double? EdgeScore, (double Score, string Reason)? Review)> RunPairAsync(
        string dbName, Dictionary<string, string> existingFields, Dictionary<string, string> incomingFields)
    {
        var store = NewStore(dbName);
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var seedBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now, CancellationToken.None);
        var existing = Record(project.Id, source.Id, seedBatch.Id, "existing-1", existingFields, now);
        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata([existing], [],
                [new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [existing.Id], CreatedAt = now }],
                [], []),
            CancellationToken.None);

        var incBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1), CancellationToken.None);
        var incoming = Record(project.Id, source.Id, incBatch.Id, "incoming-1", incomingFields, now.AddMinutes(1));
        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, incBatch.Id, [incoming], 0.90, 0.75), CancellationToken.None);

        var edges = await store.ListMatchEdgesAsync(project.Id, CancellationToken.None);
        var reviews = await store.ListReviewTasksAsync(project.Id, CancellationToken.None);
        double? edgeScore = edges.Count > 0 ? edges[0].Score : null;
        (double, string)? review = reviews.Count > 0 ? (reviews[0].Score, reviews[0].Reason) : null;
        return (edgeScore, review);
    }

    [Fact]
    public async Task SharedExactEmail_AutoMatches()
    {
        var (edge, review) = await RunPairAsync("auto.json",
            new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "alice@example.com", ["name"] = "Alice" },
            new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "alice@example.com", ["name"] = "Alice Verified" });

        Assert.NotNull(edge);
        Assert.True(edge!.Value >= 0.90);
        Assert.Null(review);
    }

    [Fact]
    public async Task FullyDisjointRecords_NoMatch()
    {
        var (edge, review) = await RunPairAsync("nomatch.json",
            new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "a@x.com", ["last_name"] = "Jones" },
            new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "b@y.com", ["last_name"] = "Smith" });

        Assert.Null(edge);
        Assert.Null(review);
    }
}
