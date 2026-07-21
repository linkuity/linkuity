using Linkuity.Core.Models;
using Linkuity.Infrastructure.Local;

namespace Linkuity.Cli.Tests;

public class LocalBatchRunnerClusterMergesTests
{
    private static LocalBatchRunner NewRunner() => new();

    private static async Task<string> CaptureAsync(LocalBatchRunner runner, string[] args)
    {
        using var output = new StringWriter();
        var previousOut = Console.Out;
        Console.SetOut(output);
        try
        {
            var exit = await runner.RunAsync(args, CancellationToken.None);
            Assert.Equal(0, exit);
        }
        finally
        {
            Console.SetOut(previousOut);
        }

        return output.ToString();
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

    // Seeds two single-member clusters (one email-bearing, one phone-bearing),
    // then ingests a bridge record sharing email with C1 and phone with C2.
    // This drives a within-batch bridge-merge and records exactly one ClusterMergeEvent
    // (Task-3 S1 scenario).
    private static async Task<(string MetadataPath, Guid ProjectId, Guid SurvivorClusterId)> SeedAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "linkuity-cluster-merges-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = path });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("MDM", "person", null, now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);

        // Seed batch: two records each in their own single-member cluster (no shared identifier -> no edge).
        var seedBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 2, now, CancellationToken.None);
        var r1 = Record(project.Id, source.Id, seedBatch.Id, "ex-1",
            new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "jon@acme.com", ["name"] = "Jonathan Smith" }, now);
        var r2 = Record(project.Id, source.Id, seedBatch.Id, "ex-2",
            new Dictionary<string, string> { ["source"] = "CRM", ["phone"] = "555-9876", ["name"] = "J Smith" }, now);
        await store.SaveCompletedBatchAsync(new CompletedBatchMetadata(
            [r1, r2], [],
            [
                new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [r1.Id], CreatedAt = now },
                new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [r2.Id], CreatedAt = now.AddSeconds(1) }
            ], [], []),
            CancellationToken.None);

        // Bridge batch: X shares email with C1 and phone with C2 -> auto-joins both -> clusters merge.
        var bridgeBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now, CancellationToken.None);
        var x = Record(project.Id, source.Id, bridgeBatch.Id, "in-x",
            new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "jon@acme.com", ["phone"] = "555-9876", ["name"] = "Jonathan Smith" }, now);
        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, bridgeBatch.Id, [x], 0.90, 0.75),
            CancellationToken.None);

        // Verify the merge happened so we have a known survivor to assert against.
        var merges = await store.ListClusterMergeEventsAsync(project.Id, CancellationToken.None);
        Assert.Single(merges);

        return (path, project.Id, merges[0].SurvivorClusterId);
    }

    [Fact]
    public async Task ClusterMerges_WritesExactHeaderAndOneDataRowWithSurvivor()
    {
        var (metadataPath, projectId, survivorClusterId) = await SeedAsync();

        var output = await CaptureAsync(NewRunner(),
        [
            "cluster", "merges",
            "--metadata", metadataPath,
            "--project-id", projectId.ToString()
        ]);

        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        // Header must match the spec exactly.
        Assert.Equal("survivor_cluster_id,absorbed_cluster_id,trigger_record_ids,score,ingest_batch_id,created_at", lines[0]);
        // Exactly one data row.
        Assert.Equal(2, lines.Length);
        // The data row leads with the survivor cluster id.
        Assert.StartsWith(survivorClusterId.ToString(), lines[1]);
    }
}
