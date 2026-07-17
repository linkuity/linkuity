using System.Text;
using Linkuity.Core.Models;
using Linkuity.Infrastructure.Local;
using Linkuity.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;

namespace Linkuity.Pipeline.Tests;

public class PostProcessingServiceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"linkuity-pipeline-{Guid.NewGuid():N}");

    [Fact]
    public async Task ProcessAsync_ReadsLocalArtifactsAndWritesGoldenRecords()
    {
        var store = CreateStore();
        var jobId = Guid.NewGuid();
        await store.WriteJsonAsync($"{jobId}/metadata.json", CreateJob(jobId));
        await UploadTextAsync(store, $"{jobId}/normalized.csv",
            """
            id,source,name,email
            1,CRM,Alice,alice@crm.example
            2,Marketing,Alice M,alice@marketing.example
            3,CRM,Bob,bob@example.com
            """);
        await UploadTextAsync(store, $"{jobId}/matches.csv",
            """
            left_id,right_id,score
            1,2,0.97
            """);
        var service = CreateService(store);

        await service.ProcessAsync(jobId.ToString());

        Assert.True(await store.ExistsAsync($"{jobId}/golden_records.csv"));
        await using var goldenStream = await store.DownloadAsync($"{jobId}/golden_records.csv");
        using var reader = new StreamReader(goldenStream, Encoding.UTF8);
        var goldenCsv = await reader.ReadToEndAsync();
        Assert.Contains("alice@crm.example", goldenCsv);
        Assert.DoesNotContain("alice@marketing.example", goldenCsv);

        var metadata = await store.ReadJsonAsync<Job>($"{jobId}/metadata.json");
        Assert.NotNull(metadata);
        Assert.Equal(JobState.Complete, metadata.State);
    }

    [Fact]
    public async Task ProcessAsync_WhenMatchesArtifactIsMissing_UpdatesMetadataToFailed()
    {
        var store = CreateStore();
        var jobId = Guid.NewGuid();
        await store.WriteJsonAsync($"{jobId}/metadata.json", CreateJob(jobId));
        await UploadTextAsync(store, $"{jobId}/normalized.csv",
            """
            id,source,name,email
            1,CRM,Alice,alice@crm.example
            """);
        var service = CreateService(store);

        await Assert.ThrowsAsync<FileNotFoundException>(() => service.ProcessAsync(jobId.ToString()));

        var metadata = await store.ReadJsonAsync<Job>($"{jobId}/metadata.json");
        Assert.NotNull(metadata);
        Assert.Equal(JobState.Failed, metadata.State);
        Assert.False(await store.ExistsAsync($"{jobId}/golden_records.csv"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private FileSystemArtifactStore CreateStore()
        => new(new FileSystemArtifactStoreOptions { RootPath = _rootPath });

    private static PostProcessingService CreateService(FileSystemArtifactStore store)
        => new(
            store,
            new GraphService(),
            new GoldenRecordService(),
            NullLogger<PostProcessingService>.Instance);

    private static async Task UploadTextAsync(FileSystemArtifactStore store, string path, string value)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(value));
        await store.UploadAsync(path, stream, "text/csv");
    }

    private static Job CreateJob(Guid jobId)
        => new()
        {
            Id = jobId,
            State = JobState.MatchingComplete,
            CreatedAt = DateTimeOffset.Parse("2026-06-12T12:00:00Z"),
            AutoStart = true,
            RecordCount = 3,
            Configuration = new MatchConfiguration
            {
                ContentType = "person",
                Fields =
                [
                    new Field { Name = "source", SemanticType = SemanticFieldType.SourceIdentifier },
                    new Field { Name = "name", SemanticType = SemanticFieldType.FullName },
                    new Field { Name = "email", SemanticType = SemanticFieldType.Email }
                ]
            },
            MergeConfiguration = new MergeConfiguration
            {
                MergeFields =
                [
                    new MergeField { FieldName = "email", SourcePriority = ["CRM", "Marketing"] }
                ]
            }
        };
}
