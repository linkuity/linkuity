using Linkuity.Core.Models;
using Linkuity.Infrastructure.Local;

namespace Linkuity.Cli.Tests;

public class LocalBatchRunnerExplainTests
{
    private static LocalBatchRunner NewRunner()
        => new();

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

    // Seeds a project with one existing record, then ingests (1) a shared-email duplicate
    // -> auto-match MatchEdge with a breakdown, and (2) a matching-last-name + real first-name
    // similarity record with no email captured -> review task with a breakdown ("Robert Smith"
    // vs "Bob Smith": last_name exact 2.0 + first_name fuzzy 0.67 over total weight 3.0 = 0.89,
    // which legitimately clears the review-floor gate). Returns the metadata path + projectId.
    private static async Task<(string MetadataPath, Guid ProjectId)> SeedAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "linkuity-explain-cli-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = path });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("MDM", "person", null, now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var seedBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now, CancellationToken.None);
        var existing = Record(project.Id, source.Id, seedBatch.Id, "CRM-7",
            new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "robert@example.com", ["first_name"] = "Robert", ["last_name"] = "Smith" }, now);
        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata(
                [existing], [],
                [new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [existing.Id], CreatedAt = now }],
                [], []),
            CancellationToken.None);

        // (1) Auto-match: shared email with CRM-7.
        var autoBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now, CancellationToken.None);
        var autoIncoming = Record(project.Id, source.Id, autoBatch.Id, "ERP-3",
            new Dictionary<string, string> { ["source"] = "ERP", ["email"] = "robert@example.com", ["first_name"] = "Robert", ["last_name"] = "Smith" }, now);
        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, autoBatch.Id, [autoIncoming], 0.90, 0.75), CancellationToken.None);

        // (2) Review: matching last name + real first-name similarity, no email captured -> review band.
        var reviewBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now, CancellationToken.None);
        var reviewIncoming = Record(project.Id, source.Id, reviewBatch.Id, "ERP-9",
            new Dictionary<string, string> { ["source"] = "ERP", ["first_name"] = "Bob", ["last_name"] = "Smith" }, now);
        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, reviewBatch.Id, [reviewIncoming], 0.90, 0.75), CancellationToken.None);

        return (path, project.Id);
    }

    [Fact]
    public async Task MatchExplain_WritesHeaderAndFactorRows()
    {
        var (metadataPath, projectId) = await SeedAsync();

        var output = await CaptureAsync(NewRunner(),
        [
            "match", "explain",
            "--metadata", metadataPath,
            "--project-id", projectId.ToString()
        ]);

        Assert.Contains("edge_id,left_record,right_record,score,decision,signal,value,weight,contribution", output);
        Assert.Contains("CRM-7", output);
        Assert.Contains("ERP-3", output);
        Assert.Contains("auto", output);
        // Reviews are excluded by default.
        Assert.DoesNotContain("review", output);
    }

    [Fact]
    public async Task MatchExplain_FiltersByRecordPair_OrderInsensitive()
    {
        var (metadataPath, projectId) = await SeedAsync();

        var output = await CaptureAsync(NewRunner(),
        [
            "match", "explain",
            "--metadata", metadataPath,
            "--project-id", projectId.ToString(),
            "--left", "CRM-7",
            "--right", "ERP-3"
        ]);

        Assert.Contains("CRM-7", output);
        Assert.Contains("ERP-3", output);
        // The review pair (ERP-9) is filtered out.
        Assert.DoesNotContain("ERP-9", output);
    }

    [Fact]
    public async Task MatchExplain_IncludeReviews_AddsReviewRows()
    {
        var (metadataPath, projectId) = await SeedAsync();

        var output = await CaptureAsync(NewRunner(),
        [
            "match", "explain",
            "--metadata", metadataPath,
            "--project-id", projectId.ToString(),
            "--include-reviews", "true"
        ]);

        Assert.Contains("edge_id,left_record,right_record,score,decision,signal,value,weight,contribution", output);
        // The review-band pair now appears with decision "review".
        Assert.Contains("ERP-9", output);
        Assert.Contains("review", output);
    }
}
