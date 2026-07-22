using System.Text;
using Linkuity.Core.Models;
using Linkuity.Infrastructure.Local;
using Linkuity.Matching.Profiles;
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
        var profile = PersonWithSourceProfile();
        var merge = new MergeConfiguration
        {
            MergeFields = [new MergeField { FieldName = "email", SourcePriority = ["CRM", "Marketing"] }]
        };

        await service.ProcessAsync(jobId.ToString(), profile, merge, CancellationToken.None);

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

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => service.ProcessAsync(jobId.ToString(), PersonWithSourceProfile(), merge: null, CancellationToken.None));

        var metadata = await store.ReadJsonAsync<Job>($"{jobId}/metadata.json");
        Assert.NotNull(metadata);
        Assert.Equal(JobState.Failed, metadata.State);
        Assert.False(await store.ExistsAsync($"{jobId}/golden_records.csv"));
    }

    [Fact]
    public async Task ProcessAsync_WithProfile_ExcludesSourceColumn_AndAppliesPriorityMerge()
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

        // Person profile has no SourceIdentifier field; organization profile declares
        // `source` as SourceIdentifier, which is what this test needs to exercise.
        var profile = DefaultMatchingProfileProvider.CreateOrganizationProfile();
        var merge = new MergeConfiguration
        {
            MergeFields = [new MergeField { FieldName = "email", SourcePriority = ["CRM", "Marketing"] }]
        };

        await service.ProcessAsync(jobId.ToString(), profile, merge, CancellationToken.None);

        Assert.True(await store.ExistsAsync($"{jobId}/golden_records.csv"));
        await using var goldenStream = await store.DownloadAsync($"{jobId}/golden_records.csv");
        using var reader = new StreamReader(goldenStream, Encoding.UTF8);
        var golden = await reader.ReadToEndAsync();

        Assert.DoesNotContain("source", golden.Split('\n')[0]); // header has no `source` column
        Assert.Contains("alice@crm.example", golden);
        Assert.DoesNotContain("alice@marketing.example", golden);
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
            RecordCount = 3
        };

    // Mirrors the person profile shape the old job configuration in CreateJob used to
    // carry (source/name/email), so ProcessAsync still resolves "source" as the
    // SourceIdentifier field for merge priority.
    private static MatchingProfile PersonWithSourceProfile() => new()
    {
        ContentType = "person",
        Fields =
        [
            new ProfileField { Name = "source", SemanticType = SemanticFieldType.SourceIdentifier, Roles = FieldRole.None },
            new ProfileField { Name = "name", SemanticType = SemanticFieldType.FullName, Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking, SimilarityEvaluator = "fuzzy", Weight = 1.5 },
            new ProfileField { Name = "email", SemanticType = SemanticFieldType.Email, Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking | FieldRole.Identifier, SimilarityEvaluator = "exact", Weight = 3.0 }
        ],
        NormalizationStrategy = "identity",
        BlockingStrategies = ["exact-value", "token-name"],
        CandidateRetrievalStrategy = "linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = "identifier-weighted",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 0.90,
        ReviewThreshold = 0.75
    };
}
