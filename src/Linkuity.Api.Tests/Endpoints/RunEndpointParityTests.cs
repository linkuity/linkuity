using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using CsvHelper;
using CsvHelper.Configuration;
using Linkuity.Api.Tests.Endpoints;

namespace Linkuity.Api.Tests.Endpoints;

public sealed class RunEndpointParityTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    public RunEndpointParityTests(TestWebApplicationFactory factory) => _factory = factory;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root containing samples/.");
    }

    private static List<HashSet<string>> ParseClusters(string csv)
    {
        using var reader = new StringReader(csv);
        using var c = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture));
        var clusters = new List<HashSet<string>>();
        c.Read(); c.ReadHeader();
        while (c.Read())
        {
            var members = (c.GetField("member_ids") ?? "")
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            clusters.Add(new HashSet<string>(members));
        }
        return clusters;
    }

    [Fact]
    public async Task PostRun_PeopleMultiSource_ClustersMatchCli()
    {
        var root = RepoRoot();
        var sampleDir = Path.Combine(root, "samples", "people-multi-source");
        var client = _factory.CreateClient();

        using var form = new MultipartFormDataContent();
        var profile = await File.ReadAllTextAsync(Path.Combine(sampleDir, "people-multi-source.profile.json"));
        var merge = await File.ReadAllTextAsync(Path.Combine(sampleDir, "people-multi-source.merge.json"));
        form.Add(new StringContent(profile), "profile");
        form.Add(new StringContent(merge), "merge-policy");
        var csvBytes = await File.ReadAllBytesAsync(Path.Combine(sampleDir, "sample.csv"));
        var csvContent = new ByteArrayContent(csvBytes);
        csvContent.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(csvContent, "file", "sample.csv");

        var response = await client.PostAsync("/run", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);

        var clusters = ParseClusters(await response.Content.ReadAsStringAsync());
        // Same expectation SampleScenarioTests pins for this sample.
        Assert.Contains(clusters, cl => new[] { "crm-050", "mkt-051", "sup-052", "bil-053" }.All(cl.Contains));
        Assert.DoesNotContain(clusters, cl => cl.Contains("crm-001") && cl.Contains("mkt-002"));
    }

    [Fact]
    public async Task PostRun_Location_CustomTaxonomy_Resolves()
    {
        var root = RepoRoot();
        var sampleDir = Path.Combine(root, "samples", "location");
        var client = _factory.CreateClient();

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(await File.ReadAllTextAsync(Path.Combine(sampleDir, "location.profile.json"))), "profile");
        form.Add(new StringContent(await File.ReadAllTextAsync(Path.Combine(sampleDir, "location.merge.json"))), "merge-policy");
        var csvContent = new ByteArrayContent(await File.ReadAllBytesAsync(Path.Combine(sampleDir, "sample.csv")));
        csvContent.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(csvContent, "file", "sample.csv");

        var response = await client.PostAsync("/run", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var clusters = ParseClusters(await response.Content.ReadAsStringAsync());
        Assert.Equal(3, clusters.Count);
        Assert.Contains(clusters, cl => new[] { "goog-001", "yelp-002", "pos-003" }.All(cl.Contains));
        Assert.DoesNotContain(clusters, cl => cl.Contains("goog-001") && cl.Contains("goog-004"));
    }
}
