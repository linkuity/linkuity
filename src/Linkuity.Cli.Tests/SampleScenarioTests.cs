using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Linkuity.Cli;

namespace Linkuity.Cli.Tests;

// Parity gate: drives each real sample under samples/ through `linkuity run` with the native
// matcher and asserts the cluster outcomes the (now-removed) Python pytest suite pinned. This
// proves no example/tutorial lesson was lost in the native rewrite, and is the tuning gate for
// the batch match cut (MatchConfigurationProfileFactory.AutoMatchThreshold).
public sealed class SampleScenarioTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), $"linkuity-samples-{Guid.NewGuid():N}");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root containing samples/.");
    }

    // Returns cluster membership as a set of id-sets, reading golden-records.csv member_ids.
    private async Task<List<HashSet<string>>> RunSampleAsync(string sampleDir, string? configOverridePath = null)
    {
        var root = RepoRoot();
        var input = Path.Combine(root, "samples", sampleDir, "sample.csv");
        var config = configOverridePath ?? Path.Combine(root, "samples", sampleDir, "match-config.json");
        var output = Path.Combine(_work, sampleDir);

        var runner = new LocalBatchRunner();
        var exit = await runner.RunAsync(
            ["run", "--input", input, "--config", config, "--output", output],
            CancellationToken.None);
        Assert.Equal(0, exit);

        var golden = Path.Combine(output, "golden-records.csv");
        using var reader = new StreamReader(golden);
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

    private static bool SameCluster(List<HashSet<string>> clusters, params string[] ids) =>
        clusters.Any(c => ids.All(c.Contains));

    private static bool SeparateClusters(List<HashSet<string>> clusters, string a, string b) =>
        !clusters.Any(c => c.Contains(a) && c.Contains(b));

    [Theory]
    [InlineData("crm-010", "mkt-011")]
    [InlineData("crm-030", "sup-031", "bil-032")]
    [InlineData("crm-050", "mkt-051", "sup-052", "bil-053")]
    [InlineData("crm-080", "crm-081", "sup-082", "mkt-083", "mkt-084")]
    public async Task PeopleMultiSource_ExpectedClustersFormOneComponent(params string[] members)
    {
        var clusters = await RunSampleAsync("people-multi-source");
        Assert.True(SameCluster(clusters, members), $"Expected {string.Join(",", members)} in one cluster.");
    }

    [Fact]
    public async Task PeopleMultiSource_SingletonsStaySeparate()
    {
        var clusters = await RunSampleAsync("people-multi-source");
        Assert.True(SeparateClusters(clusters, "crm-001", "mkt-002"));
    }

    [Fact]
    public async Task PhoneNoise_TwinsStaySeparateWhenPhoneExcluded()
    {
        // Production config for this sample excludes phone from matching.
        var clusters = await RunSampleAsync("people-phone-noise");
        Assert.True(SeparateClusters(clusters, "crm-001", "crm-002"),
            "With phone excluded, the twins must stay as separate golden records.");
    }

    [Fact]
    public async Task PhoneNoise_TwinsMergeWhenPhoneIncluded()
    {
        // Contrast: flip phone to participatesInMatching=true and the twins false-merge.
        var configPath = WritePhoneIncludedConfig();
        var clusters = await RunSampleAsync("people-phone-noise", configPath);
        Assert.True(SameCluster(clusters, "crm-001", "crm-002"),
            "With phone included, the twins should false-merge into one cluster.");
    }

    [Fact]
    public async Task OrgNameNoise_AcmesStaySeparateWithDomain()
    {
        var clusters = await RunSampleAsync("organizations-name-noise");
        Assert.True(SeparateClusters(clusters, "crm-001", "mkt-002"),
            "With domain_name in matching, the near-twin Acmes must stay separate.");
    }

    [Fact]
    public async Task OrgNameNoise_AmpersandVariantsCluster()
    {
        var clusters = await RunSampleAsync("organizations-name-noise");
        Assert.True(SameCluster(clusters, "crm-010", "mkt-011", "sup-012"),
            "Ampersand/`and`/nothing variants of the same firm must cluster.");
    }

    [Theory]
    [InlineData("crm-010", "mkt-011")]
    [InlineData("crm-050", "mkt-051", "sup-052", "fin-053")]
    [InlineData("crm-060", "mkt-061", "sup-062", "fin-063")]
    [InlineData("crm-080", "crm-081", "sup-082", "mkt-083", "mkt-084")]
    public async Task OrgMultiSource_ExpectedClustersFormOneComponent(params string[] members)
    {
        var clusters = await RunSampleAsync("organizations-multi-source");
        Assert.True(SameCluster(clusters, members), $"Expected {string.Join(",", members)} in one cluster.");
    }

    [Fact]
    public async Task OrgMultiSource_SingletonsStaySeparate()
    {
        var clusters = await RunSampleAsync("organizations-multi-source");
        Assert.True(SeparateClusters(clusters, "crm-001", "mkt-002"));
    }

    private string WritePhoneIncludedConfig()
    {
        var root = RepoRoot();
        var original = File.ReadAllText(Path.Combine(root, "samples", "people-phone-noise", "match-config.json"));
        // Remove the phone exclusion so phone participates (default is participatesInMatching=true).
        // NOTE: the source file pads "phone", with 10 spaces before "semanticType" (not 8, not 9) —
        // verified against the literal file content; this Replace is a no-op silently otherwise.
        var modified = original.Replace(
            "{ \"name\": \"phone\",          \"semanticType\": \"phone\", \"participatesInMatching\": false }",
            "{ \"name\": \"phone\",          \"semanticType\": \"phone\" }");
        Assert.NotEqual(original, modified); // guards against a silent no-op Replace
        Directory.CreateDirectory(_work);
        var path = Path.Combine(_work, "phone-included-config.json");
        File.WriteAllText(path, modified);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }
}
