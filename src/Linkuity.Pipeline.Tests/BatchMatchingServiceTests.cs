using System.Text;
using Linkuity.Core.Models;
using Linkuity.Infrastructure.Local;

namespace Linkuity.Pipeline.Tests;

public sealed class BatchMatchingServiceTests
{
    private static MatchConfiguration PersonConfig() => new()
    {
        ContentType = "person",
        Fields =
        [
            new Field { Name = "first_name", SemanticType = SemanticFieldType.FirstName },
            new Field { Name = "last_name", SemanticType = SemanticFieldType.LastName },
            new Field { Name = "email", SemanticType = SemanticFieldType.Email }
        ]
    };

    private static (string, IReadOnlyDictionary<string, string>) Row(
        string id, string first, string last, string email) =>
        (id, new Dictionary<string, string>
        {
            ["id"] = id, ["first_name"] = first, ["last_name"] = last, ["email"] = email
        });

    private static List<string[]> ParseCsv(string csv)
    {
        var lines = csv.Replace("\r\n", "\n").Trim().Split('\n');
        return lines.Select(l => l.Split(',')).ToList();
    }

    [Fact]
    public void BuildMatchesCsv_HasExpectedHeader()
    {
        var csv = BatchMatchingService.BuildMatchesCsv([Row("a", "Ada", "Lovelace", "ada@x.com")], PersonConfig());
        var header = ParseCsv(csv)[0];
        Assert.Equal(["left_id", "right_id", "similarity", "fuzzy_similarity"], header);
    }

    [Fact]
    public void BuildMatchesCsv_IdenticalRecordsProduceOneEdge()
    {
        var rows = new List<(string, IReadOnlyDictionary<string, string>)>
        {
            Row("a", "Ada", "Lovelace", "ada@x.com"),
            Row("b", "Ada", "Lovelace", "ada@x.com")
        };
        var csv = BatchMatchingService.BuildMatchesCsv(rows, PersonConfig());
        var dataRows = ParseCsv(csv).Skip(1).ToList();
        var edge = Assert.Single(dataRows);
        Assert.Equal(new HashSet<string> { "a", "b" }, new HashSet<string> { edge[0], edge[1] });
    }

    [Fact]
    public void BuildMatchesCsv_DissimilarRecordsProduceNoEdges()
    {
        var rows = new List<(string, IReadOnlyDictionary<string, string>)>
        {
            Row("a", "Ada", "Lovelace", "ada@x.com"),
            Row("b", "Zed", "Quixote", "zed@y.com")
        };
        var csv = BatchMatchingService.BuildMatchesCsv(rows, PersonConfig());
        Assert.Empty(ParseCsv(csv).Skip(1));
    }

    [Fact]
    public void BuildMatchesCsv_ExcludesSelfPairs()
    {
        var rows = new List<(string, IReadOnlyDictionary<string, string>)>
        {
            Row("a", "Ada", "Lovelace", "ada@x.com"),
            Row("b", "Ada", "Lovelace", "ada@x.com")
        };
        var csv = BatchMatchingService.BuildMatchesCsv(rows, PersonConfig());
        foreach (var edge in ParseCsv(csv).Skip(1))
            Assert.NotEqual(edge[0], edge[1]);
    }
}

public sealed class BatchMatchingServiceRunTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bms-{Guid.NewGuid():N}");

    [Fact]
    public async Task RunAsync_WritesMatchesForIdenticalRecords()
    {
        var store = new FileSystemArtifactStore(new FileSystemArtifactStoreOptions { RootPath = _root });
        var jobId = Guid.NewGuid().ToString();
        var config = new MatchConfiguration
        {
            ContentType = "person",
            Fields =
            [
                new Field { Name = "first_name", SemanticType = SemanticFieldType.FirstName },
                new Field { Name = "last_name", SemanticType = SemanticFieldType.LastName },
                new Field { Name = "email", SemanticType = SemanticFieldType.Email }
            ]
        };
        await store.WriteJsonAsync($"{jobId}/metadata.json",
            new Job { Id = Guid.Parse(jobId), State = JobState.Processing, CreatedAt = DateTimeOffset.UtcNow, Configuration = config, AutoStart = false });
        var normalized = "id,first_name,last_name,email\na,Ada,Lovelace,ada@x.com\nb,Ada,Lovelace,ada@x.com\n";
        using (var s = new MemoryStream(Encoding.UTF8.GetBytes(normalized)))
            await store.UploadAsync($"{jobId}/normalized.csv", s, "text/csv");

        await new BatchMatchingService(store).RunAsync(jobId, CancellationToken.None);

        await using var outStream = await store.DownloadAsync($"{jobId}/matches.csv");
        using var reader = new StreamReader(outStream);
        var csv = await reader.ReadToEndAsync();
        Assert.Contains("a,b", csv.Replace("\r\n", "\n"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
