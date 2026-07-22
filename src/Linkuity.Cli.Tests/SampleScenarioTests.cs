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
    private async Task<List<HashSet<string>>> RunSampleAsync(string sampleDir, string? profileOverridePath = null)
    {
        var root = RepoRoot();
        var dir = Path.Combine(root, "samples", sampleDir);
        var input = Path.Combine(dir, "sample.csv");
        var profile = profileOverridePath ?? Directory.GetFiles(dir, "*.profile.json").Single();
        var mergeFiles = Directory.GetFiles(dir, "*.merge.json");
        var output = Path.Combine(_work, sampleDir);

        var args = new List<string> { "run", "--input", input, "--profile", profile, "--output", output };
        if (mergeFiles.Length == 1) { args.Add("--merge-policy"); args.Add(mergeFiles[0]); }

        var runner = new LocalBatchRunner();
        var exit = await runner.RunAsync(args.ToArray(), CancellationToken.None);
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
        // Contrast: give phone matching roles and the twins false-merge.
        var clusters = await RunSampleAsync("people-phone-noise", WritePhoneIncludedProfile());
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

    [Fact]
    public async Task Location_SameVenueClusters_DifferentLocationsStaySeparate()
    {
        var clusters = await RunSampleAsync("location");
        Assert.Equal(3, clusters.Count);
        Assert.True(SameCluster(clusters, "goog-001", "yelp-002", "pos-003"));   // one venue, three sources
        Assert.True(SameCluster(clusters, "goog-004", "yelp-005"));              // second venue
        Assert.True(SeparateClusters(clusters, "goog-001", "goog-004"));         // same chain+domain, different venue
    }

    [Fact]
    public async Task OrgMultiSource_BuiltInProfileByName_MatchesFileProfile()
    {
        var clusters = await RunSampleAsync("organizations-multi-source", profileOverridePath: "organization");
        Assert.True(SameCluster(clusters, "crm-050", "mkt-051", "sup-052", "fin-053"));
        Assert.True(SeparateClusters(clusters, "crm-001", "mkt-002"));
    }

    private string WritePhoneIncludedProfile()
    {
        var root = RepoRoot();
        var original = File.ReadAllText(Path.Combine(root, "samples", "people-phone-noise", "people-phone-noise.profile.json"));
        var modified = original.Replace(
            "{ \"name\": \"phone\",         \"semanticType\": \"Phone\",            \"roles\": [] }",
            "{ \"name\": \"phone\", \"semanticType\": \"Phone\", \"roles\": [\"Matchable\",\"Blocking\",\"Identifier\"], \"similarityEvaluator\": \"exact\", \"weight\": 3.0 }");
        Assert.NotEqual(original, modified); // guards against a silent no-op Replace
        Directory.CreateDirectory(_work);
        var path = Path.Combine(_work, "phone-included.profile.json");
        File.WriteAllText(path, modified);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }
}
