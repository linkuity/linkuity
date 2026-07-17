using Linkuity.Cli;
using Linkuity.Core.Models;

namespace Linkuity.Cli.Tests;

public sealed class NativeMatchingProcessTests
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
        var csv = NativeMatchingProcess.BuildMatchesCsv([Row("a", "Ada", "Lovelace", "ada@x.com")], PersonConfig());
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
        var csv = NativeMatchingProcess.BuildMatchesCsv(rows, PersonConfig());
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
        var csv = NativeMatchingProcess.BuildMatchesCsv(rows, PersonConfig());
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
        var csv = NativeMatchingProcess.BuildMatchesCsv(rows, PersonConfig());
        foreach (var edge in ParseCsv(csv).Skip(1))
            Assert.NotEqual(edge[0], edge[1]);
    }
}
