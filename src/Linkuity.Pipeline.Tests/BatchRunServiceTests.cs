using System.Text;
using Linkuity.Core.Models;
using Linkuity.Infrastructure.Local;
using Microsoft.Extensions.Logging.Abstractions;

namespace Linkuity.Pipeline.Tests;

public sealed class BatchRunServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"brs-{Guid.NewGuid():N}");

    private BatchRunService Build(FileSystemArtifactStore store) => new(
        new CsvNormalizationService(store),
        new BatchMatchingService(store),
        new PostProcessingService(store, new GraphService(), new GoldenRecordService(), NullLogger<PostProcessingService>.Instance),
        store);

    [Fact]
    public async Task RunAsync_ProducesCompleteJobAndGoldenRecords()
    {
        var store = new FileSystemArtifactStore(new FileSystemArtifactStoreOptions { RootPath = _root });
        var request = new CreateJobRequest
        {
            Configuration = new MatchConfiguration
            {
                ContentType = "person",
                Fields =
                [
                    new Field { Name = "first_name", SemanticType = SemanticFieldType.FirstName },
                    new Field { Name = "last_name", SemanticType = SemanticFieldType.LastName },
                    new Field { Name = "email", SemanticType = SemanticFieldType.Email }
                ]
            }
        };
        var input = "first_name,last_name,email,id\nAda,Lovelace,ada@x.com,a\nAda,Lovelace,ada@x.com,b\n";
        using var csv = new MemoryStream(Encoding.UTF8.GetBytes(input));

        var result = await Build(store).RunAsync(request, csv, CancellationToken.None);

        var job = await store.ReadJsonAsync<Job>($"{result.JobId}/metadata.json");
        Assert.NotNull(job);
        Assert.Equal(JobState.Complete, job!.State);
        Assert.True(await store.ExistsAsync($"{result.JobId}/golden_records.csv"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
