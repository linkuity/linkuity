using System.Text;
using Linkuity.Core.Models;
using Linkuity.Infrastructure.Local;

namespace Linkuity.Infrastructure.Local.Tests;

public class FileSystemArtifactStoreTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"linkuity-artifacts-{Guid.NewGuid():N}");

    [Fact]
    public async Task UploadAsync_CreatesNestedDirectoriesAndDownloadAsyncReturnsBytes()
    {
        var store = CreateStore();
        await using var upload = new MemoryStream(Encoding.UTF8.GetBytes("id,name\n1,Ada\n"));

        await store.UploadAsync("jobs/abc/input.csv", upload, "text/csv");

        await using var download = await store.DownloadAsync("jobs/abc/input.csv");
        using var reader = new StreamReader(download, Encoding.UTF8);
        Assert.Equal("id,name\n1,Ada\n", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task ExistsAsync_ReturnsTrueOnlyForExistingArtifacts()
    {
        var store = CreateStore();
        await using var upload = new MemoryStream(Encoding.UTF8.GetBytes("matches"));

        await store.UploadAsync("job-1/matches.csv", upload, "text/csv");

        Assert.True(await store.ExistsAsync("job-1/matches.csv"));
        Assert.False(await store.ExistsAsync("job-1/missing.csv"));
    }

    [Fact]
    public async Task WriteJsonAsync_AndReadJsonAsync_RoundTripMetadataWithSnakeCaseEnums()
    {
        var store = CreateStore();
        var job = new Job
        {
            Id = Guid.NewGuid(),
            State = JobState.MatchingComplete,
            CreatedAt = DateTimeOffset.Parse("2026-06-11T12:00:00Z"),
            AutoStart = true,
            RecordCount = 42,
            Configuration = new MatchConfiguration
            {
                ContentType = "person",
                Fields =
                [
                    new Field
                    {
                        Name = "last_name",
                        SemanticType = SemanticFieldType.LastName
                    }
                ]
            }
        };

        await store.WriteJsonAsync($"{job.Id}/metadata.json", job);

        var json = await File.ReadAllTextAsync(Path.Combine(_rootPath, job.Id.ToString(), "metadata.json"));
        Assert.Contains("\"State\":\"matching_complete\"", json);
        var roundTrip = await store.ReadJsonAsync<Job>($"{job.Id}/metadata.json");
        Assert.NotNull(roundTrip);
        Assert.Equal(JobState.MatchingComplete, roundTrip.State);
        Assert.Equal(SemanticFieldType.LastName, roundTrip.Configuration.Fields[0].SemanticType);
    }

    [Fact]
    public async Task CurrentJobArtifacts_CanReadAndWriteAllCurrentJobArtifactNames()
    {
        var store = CreateStore();
        var jobId = Guid.NewGuid().ToString();

        await store.WriteJsonAsync($"{jobId}/metadata.json", new Job
        {
            Id = Guid.Parse(jobId),
            State = JobState.Open,
            CreatedAt = DateTimeOffset.Parse("2026-06-11T12:00:00Z"),
            AutoStart = false,
            Configuration = new MatchConfiguration
            {
                ContentType = "person",
                Fields = []
            }
        });

        foreach (var artifactName in new[] { "input.csv", "normalized.csv", "matches.csv", "golden_records.csv" })
        {
            await using var upload = new MemoryStream(Encoding.UTF8.GetBytes($"artifact,{artifactName}\n"));
            await store.UploadAsync($"{jobId}/{artifactName}", upload, "text/csv");
        }

        Assert.NotNull(await store.ReadJsonAsync<Job>($"{jobId}/metadata.json"));
        foreach (var artifactName in new[] { "input.csv", "normalized.csv", "matches.csv", "golden_records.csv" })
            Assert.True(await store.ExistsAsync($"{jobId}/{artifactName}"));
    }

    [Theory]
    [InlineData("../escape.csv")]
    [InlineData("job/../../escape.csv")]
    [InlineData("/escape.csv")]
    [InlineData("C:/escape.csv")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UploadAsync_RejectsUnsafePaths(string artifactPath)
    {
        var store = CreateStore();
        await using var upload = new MemoryStream(Encoding.UTF8.GetBytes("escape"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.UploadAsync(artifactPath, upload, "text/csv"));
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("job/../../escape.json")]
    [InlineData("/escape.json")]
    [InlineData("C:/escape.json")]
    public async Task ReadJsonAsync_RejectsUnsafePaths(string artifactPath)
    {
        var store = CreateStore();

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.ReadJsonAsync<Job>(artifactPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private FileSystemArtifactStore CreateStore()
        => new(new FileSystemArtifactStoreOptions { RootPath = _rootPath });
}
