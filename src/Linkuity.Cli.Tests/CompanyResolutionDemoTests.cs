using System.Globalization;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using Linkuity.Cli;

namespace Linkuity.Cli.Tests;

// Regression pin for the company-resolution hero demo (showcases/company-resolution).
// Runs the committed projected input through `linkuity run` in-process and asserts:
//   (1) the golden-record count matches the pinned expectations.json,
//   (2) NO cluster mixes two distinct companies (zero incorrect merges),
//   (3) the marquee "mustUnify" companies each land in exactly one cluster.
public sealed class CompanyResolutionDemoTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), $"linkuity-hero-{Guid.NewGuid():N}");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "showcases")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root containing showcases/.");
    }

    private sealed record Expectations(int goldenRecordCount, int maxIncorrectMerges, string[] mustUnify);

    private static Dictionary<string, string> LoadGroundTruth(string path)
    {
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture));
        var map = new Dictionary<string, string>();
        csv.Read(); csv.ReadHeader();
        while (csv.Read())
            map[csv.GetField("record_id")!] = csv.GetField("canonical_key")!;
        return map;
    }

    private async Task<List<HashSet<string>>> RunDemoAsync(string demoDir)
    {
        var input   = Path.Combine(demoDir, "run", "companies.csv");
        var profile = Path.Combine(demoDir, "run", "company.profile.json");
        var merge   = Path.Combine(demoDir, "run", "company.merge.json");
        var output  = Path.Combine(_work, "out");

        var args = new[] { "run", "--input", input, "--profile", profile, "--merge-policy", merge, "--output", output };
        var exit = await new LocalBatchRunner().RunAsync(args, CancellationToken.None);
        Assert.Equal(0, exit);

        using var reader = new StreamReader(Path.Combine(output, "golden-records.csv"));
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture));
        var clusters = new List<HashSet<string>>();
        csv.Read(); csv.ReadHeader();
        while (csv.Read())
        {
            var members = (csv.GetField("member_ids") ?? "")
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            clusters.Add(new HashSet<string>(members));
        }
        return clusters;
    }

    [Fact]
    public async Task Demo_MatchesPinnedOutcome_WithZeroIncorrectMerges()
    {
        var demoDir = Path.Combine(RepoRoot(), "showcases", "company-resolution");
        var truth = LoadGroundTruth(Path.Combine(demoDir, "validate", "ground-truth.csv"));
        var exp = JsonSerializer.Deserialize<Expectations>(
            File.ReadAllText(Path.Combine(demoDir, "run", "expectations.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var clusters = await RunDemoAsync(demoDir);

        // (1) pinned golden-record count
        Assert.Equal(exp.goldenRecordCount, clusters.Count);

        // (2) zero clusters mixing two distinct companies
        var mixed = clusters
            .Select(c => c.Select(id => truth.TryGetValue(id, out var k) ? k : "?").Distinct().ToArray())
            .Where(keys => keys.Length > 1)
            .ToList();
        Assert.True(mixed.Count <= exp.maxIncorrectMerges,
            $"Incorrect merges: {string.Join(" ; ", mixed.Select(k => string.Join("+", k)))}");

        // (3) each marquee company is wholly within one cluster
        foreach (var key in exp.mustUnify)
        {
            var ids = truth.Where(kv => kv.Value == key).Select(kv => kv.Key).ToHashSet();
            Assert.True(clusters.Any(c => ids.All(c.Contains)),
                $"Company '{key}' should resolve into a single cluster.");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }
}
