using Linkuity.Cli;
using Linkuity.Infrastructure.Local;

namespace Linkuity.Cli.Tests;

public sealed class LocalBatchRunnerProfilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"linkuity-cli-profiles-{Guid.NewGuid():N}");

    private const string OrganizationProfileJson = """
    {
      "contentType": "organization",
      "fields": [
        { "name": "source",            "semanticType": "SourceIdentifier", "roles": [] },
        { "name": "organization_name", "semanticType": "OrganizationName", "roles": ["Searchable","Matchable","Blocking"], "similarityEvaluator": "fuzzy", "weight": 2.0 },
        { "name": "domain_name",       "semanticType": "DomainName",       "roles": ["Searchable","Matchable","Blocking","Identifier"], "similarityEvaluator": "exact", "weight": 2.5 },
        { "name": "email",             "semanticType": "Email",            "roles": ["Searchable","Matchable","Blocking","Identifier"], "similarityEvaluator": "exact", "weight": 2.5 },
        { "name": "phone",             "semanticType": "Phone",            "roles": ["Matchable","Blocking","Identifier"],              "similarityEvaluator": "exact", "weight": 2.0 }
      ],
      "normalizationStrategy": "identity",
      "blockingStrategies": ["exact-value", "token-name"],
      "candidateRetrievalStrategy": "linear",
      "similarityStrategy": "field-weighted",
      "scoringStrategy": "identifier-weighted",
      "decisionStrategy": "threshold",
      "clusteringStrategy": "union-find",
      "autoMatchThreshold": 0.90,
      "reviewThreshold": 0.75
    }
    """;

    [Fact]
    public async Task IngestIncremental_WithOrganizationProfile_AutoMatchesOnSharedDomain()
    {
        Directory.CreateDirectory(_root);
        var metadataPath = Path.Combine(_root, "metadata.json");
        var profilePath = Path.Combine(_root, "organization.profile.json");
        await File.WriteAllTextAsync(profilePath, OrganizationProfileJson);

        var seedCsv = Path.Combine(_root, "seed.csv");
        await File.WriteAllTextAsync(seedCsv,
            "id,source,organization_name,domain_name,email,phone\n" +
            "crm-001,CRM,Acme Industries,acme.com,info@acme.com,+1-415-555-0100\n");
        var matchCsv = Path.Combine(_root, "match.csv");
        await File.WriteAllTextAsync(matchCsv,
            "id,source,organization_name,domain_name,email,phone\n" +
            "mkt-010,Marketing,Acme Industries,acme.com,hello@acme.com,+1-415-555-0100\n");

        var runner = new LocalBatchRunner();

        Assert.Equal(0, await runner.RunAsync(
            ["project", "create", "--metadata", metadataPath, "--name", "Org MDM", "--content-type", "organization"],
            CancellationToken.None));
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = metadataPath });
        var project = Assert.Single(await store.ListProjectsAsync(CancellationToken.None));

        Assert.Equal(0, await runner.RunAsync(
            ["source", "create", "--metadata", metadataPath, "--project-id", project.Id.ToString(), "--name", "Import"],
            CancellationToken.None));
        var source = Assert.Single(await store.ListSourcesAsync(project.Id, CancellationToken.None));

        Assert.Equal(0, await runner.RunAsync(
            ["batch", "create", "--metadata", metadataPath, "--project-id", project.Id.ToString(), "--source-id", source.Id.ToString(), "--record-count", "1"],
            CancellationToken.None));
        var seedBatch = (await store.ListIngestBatchesAsync(project.Id, CancellationToken.None))[0];

        Assert.Equal(0, await runner.RunAsync(
            ["ingest-incremental", "--metadata", metadataPath, "--project-id", project.Id.ToString(), "--source-id", source.Id.ToString(),
             "--batch-id", seedBatch.Id.ToString(), "--input", seedCsv, "--profiles", profilePath],
            CancellationToken.None));

        Assert.Equal(0, await runner.RunAsync(
            ["batch", "create", "--metadata", metadataPath, "--project-id", project.Id.ToString(), "--source-id", source.Id.ToString(), "--record-count", "1"],
            CancellationToken.None));
        var matchBatch = (await store.ListIngestBatchesAsync(project.Id, CancellationToken.None))
            .OrderBy(b => b.CreatedAt).Last();

        Assert.Equal(0, await runner.RunAsync(
            ["ingest-incremental", "--metadata", metadataPath, "--project-id", project.Id.ToString(), "--source-id", source.Id.ToString(),
             "--batch-id", matchBatch.Id.ToString(), "--input", matchCsv, "--profiles", profilePath],
            CancellationToken.None));

        var clusters = await store.ListClustersAsync(project.Id, CancellationToken.None);
        Assert.Single(clusters); // the two Acme records (shared domain) auto-merged into one cluster
        Assert.Equal(2, clusters[0].MemberEntityRecordIds.Count);
    }

    private const string OrganizationOverrideProfileJson = """
    {
      "contentType": "organization",
      "fields": [
        { "name": "source",            "semanticType": "SourceIdentifier", "roles": [] },
        { "name": "organization_name", "semanticType": "OrganizationName", "roles": ["Searchable","Matchable","Blocking"], "similarityEvaluator": "fuzzy", "weight": 2.0 },
        { "name": "domain_name",       "semanticType": "DomainName",       "roles": ["Searchable","Matchable","Blocking","Identifier"], "similarityEvaluator": "exact", "weight": 2.5 },
        { "name": "email",             "semanticType": "Email",            "roles": ["Searchable","Matchable","Blocking","Identifier"], "similarityEvaluator": "exact", "weight": 2.5 },
        { "name": "phone",             "semanticType": "Phone",            "roles": ["Matchable","Blocking","Identifier"],              "similarityEvaluator": "exact", "weight": 2.0 }
      ],
      "normalizationStrategy": "identity",
      "blockingStrategies": ["exact-value", "token-name"],
      "candidateRetrievalStrategy": "linear",
      "similarityStrategy": "field-weighted",
      "scoringStrategy": "identifier-weighted",
      "decisionStrategy": "threshold",
      "clusteringStrategy": "union-find",
      "autoMatchThreshold": 0.90,
      "reviewThreshold": 0.85
    }
    """;

    [Fact]
    public async Task IngestIncremental_OrganizationBuiltIn_ResolvesWithoutProfilesOption()
    {
        Directory.CreateDirectory(_root);
        var metadataPath = Path.Combine(_root, "metadata.json");
        var seedCsv = Path.Combine(_root, "seed.csv");
        await File.WriteAllTextAsync(seedCsv,
            "id,source,organization_name,domain_name,email,phone\n" +
            "crm-001,CRM,Acme Industries,acme.com,info@acme.com,+1-415-555-0100\n");
        var matchCsv = Path.Combine(_root, "match.csv");
        await File.WriteAllTextAsync(matchCsv,
            "id,source,organization_name,domain_name,email,phone\n" +
            "mkt-010,Marketing,Acme Industries,acme.com,hello@acme.com,+1-415-555-0100\n");

        var runner = new LocalBatchRunner();
        var (project, source) = await CreateProjectAndSourceAsync(runner, metadataPath, "organization");

        await IngestAsync(runner, metadataPath, project, source, seedCsv, profilesPath: null);
        await IngestAsync(runner, metadataPath, project, source, matchCsv, profilesPath: null);

        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = metadataPath });
        var clusters = await store.ListClustersAsync(project, CancellationToken.None);
        Assert.Single(clusters); // built-in organization profile auto-merged the shared-domain pair
    }

    [Fact]
    public async Task IngestIncremental_LoadedProfile_OverridesBuiltInThresholds()
    {
        Directory.CreateDirectory(_root);
        var metadataPath = Path.Combine(_root, "metadata.json");
        var overridePath = Path.Combine(_root, "organization-override.profile.json");
        await File.WriteAllTextAsync(overridePath, OrganizationOverrideProfileJson);

        // The two records share the IDENTICAL organization_name "Acme Industries"
        // (token-name blocking keys on the LAST name token, so identical names share a
        // blocking key), but have different domain/email/phone (no identifier match), so
        // the identifier-weighted scorer floors them to exactly 0.80. The override raises
        // reviewThreshold to 0.85, so 0.80 < 0.85 -> no review, two singleton clusters.
        // (Under the built-in profile's 0.75 review threshold this same pair is a review.)
        var seedCsv = Path.Combine(_root, "seed.csv");
        await File.WriteAllTextAsync(seedCsv,
            "id,source,organization_name,domain_name,email,phone\n" +
            "crm-001,CRM,Acme Industries,acme.com,info@acme.com,+1-415-555-0100\n");
        var matchCsv = Path.Combine(_root, "match.csv");
        await File.WriteAllTextAsync(matchCsv,
            "id,source,organization_name,domain_name,email,phone\n" +
            "web-020,Web,Acme Industries,acme.io,team@acme.io,+1-650-555-0199\n");

        var runner = new LocalBatchRunner();
        var (project, source) = await CreateProjectAndSourceAsync(runner, metadataPath, "organization");

        await IngestAsync(runner, metadataPath, project, source, seedCsv, overridePath);
        await IngestAsync(runner, metadataPath, project, source, matchCsv, overridePath);

        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = metadataPath });
        Assert.Equal(2, (await store.ListClustersAsync(project, CancellationToken.None)).Count);
        Assert.Empty(await store.ListReviewTasksAsync(project, CancellationToken.None));
    }

    private static async Task<(Guid Project, Guid Source)> CreateProjectAndSourceAsync(
        LocalBatchRunner runner, string metadataPath, string contentType)
    {
        Assert.Equal(0, await runner.RunAsync(
            ["project", "create", "--metadata", metadataPath, "--name", "Org MDM", "--content-type", contentType],
            CancellationToken.None));
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = metadataPath });
        var project = Assert.Single(await store.ListProjectsAsync(CancellationToken.None));
        Assert.Equal(0, await runner.RunAsync(
            ["source", "create", "--metadata", metadataPath, "--project-id", project.Id.ToString(), "--name", "Import"],
            CancellationToken.None));
        var source = Assert.Single(await store.ListSourcesAsync(project.Id, CancellationToken.None));
        return (project.Id, source.Id);
    }

    private static async Task IngestAsync(
        LocalBatchRunner runner, string metadataPath, Guid project, Guid source, string inputCsv, string? profilesPath)
    {
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = metadataPath });
        Assert.Equal(0, await runner.RunAsync(
            ["batch", "create", "--metadata", metadataPath, "--project-id", project.ToString(), "--source-id", source.ToString(), "--record-count", "1"],
            CancellationToken.None));
        var batch = (await store.ListIngestBatchesAsync(project, CancellationToken.None)).OrderBy(b => b.CreatedAt).Last();

        var args = new List<string>
        {
            "ingest-incremental", "--metadata", metadataPath, "--project-id", project.ToString(),
            "--source-id", source.ToString(), "--batch-id", batch.Id.ToString(), "--input", inputCsv
        };
        if (profilesPath is not null)
        {
            args.Add("--profiles");
            args.Add(profilesPath);
        }
        Assert.Equal(0, await runner.RunAsync(args.ToArray(), CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
