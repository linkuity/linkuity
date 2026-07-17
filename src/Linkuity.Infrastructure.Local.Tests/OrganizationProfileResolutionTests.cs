using Linkuity.Core.Models;
using Linkuity.Infrastructure.Local;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Profiles.Configuration;

namespace Linkuity.Infrastructure.Local.Tests;

public sealed class OrganizationProfileResolutionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"linkuity-org-{Guid.NewGuid():N}.json");

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

    private FileMetadataStore NewOrgStore()
    {
        var orgProfile = new MatchingProfileConfigLoader().LoadFromJson(OrganizationProfileJson, MatchingDefaults.CreateRegistry());
        var provider = new DefaultMatchingProfileProvider([DefaultMatchingProfileProvider.CreatePersonProfile(), orgProfile]);
        return new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = _path }, engine: null, provider, indexedRetrieval: null);
    }

    private static EntityRecord Record(Guid projectId, Guid sourceId, Guid batchId, string id, Dictionary<string, string> fields) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        SourceId = sourceId,
        IngestBatchId = batchId,
        SourceRecordId = id,
        Fields = fields,
        BlockingKeys = [],
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static async Task<IngestBatch> IngestAsync(FileMetadataStore store, Guid projectId, Guid sourceId, params EntityRecord[] records)
    {
        var batch = await store.CreateIngestBatchAsync(projectId, sourceId, null, records.Length, DateTimeOffset.UtcNow, CancellationToken.None);
        var withBatch = records.Select(r => Record(projectId, sourceId, batch.Id, r.SourceRecordId, new Dictionary<string, string>(r.Fields))).ToList();
        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(projectId, sourceId, batch.Id, withBatch, 0.90, 0.75), CancellationToken.None);
        return batch;
    }

    [Fact]
    public async Task OrganizationDataset_ResolvesEndToEnd_WithConfigOnlyProfile()
    {
        var store = NewOrgStore();
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Org MDM", "organization", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "Import", now, CancellationToken.None);

        // Seed: three distinct organizations.
        await IngestAsync(store, project.Id, source.Id,
            Record(project.Id, source.Id, Guid.Empty, "crm-001", new() { ["source"] = "CRM", ["organization_name"] = "Acme Industries", ["domain_name"] = "acme.com", ["email"] = "info@acme.com", ["phone"] = "+1-415-555-0100" }),
            Record(project.Id, source.Id, Guid.Empty, "fin-002", new() { ["source"] = "Finance", ["organization_name"] = "Globex LLC", ["domain_name"] = "globex.com", ["email"] = "ap@globex.com", ["phone"] = "+1-212-555-0144" }),
            Record(project.Id, source.Id, Guid.Empty, "crm-003", new() { ["source"] = "CRM", ["organization_name"] = "Initech Inc", ["domain_name"] = "initech.io", ["email"] = "contact@initech.io", ["phone"] = "+1-512-555-0170" }));

        Assert.Equal(3, (await store.ListClustersAsync(project.Id, CancellationToken.None)).Count);

        // Near-match name (shares the last token "Industries" so token-name blocking still finds the
        // candidate), no domain/email/phone captured (aggregator scrape with no identifiers) →
        // organization_name is the only comparable field: "Acme Industries" vs "Zeta Industries"
        // fuzzy 0.80, which is the whole weighted score (no identifier field dilutes or floors it) →
        // review, not auto-merged (0.80 clears the review-floor gate but stays under the 0.90 auto
        // threshold).
        var webBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, DateTimeOffset.UtcNow, CancellationToken.None);
        var webResult = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id,
                webBatch.Id,
                [Record(project.Id, source.Id, webBatch.Id, "web-020", new() { ["source"] = "Web", ["organization_name"] = "Zeta Industries" })],
                0.90, 0.75),
            CancellationToken.None);
        Assert.Equal(0, webResult.AutoMatches);
        Assert.Equal(1, webResult.ReviewTasks);
        Assert.Equal(4, (await store.ListClustersAsync(project.Id, CancellationToken.None)).Count);

        // Same name, SAME domain → auto-match into the Acme cluster.
        var mktBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, DateTimeOffset.UtcNow, CancellationToken.None);
        var mktResult = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id,
                mktBatch.Id,
                [Record(project.Id, source.Id, mktBatch.Id, "mkt-010", new() { ["source"] = "Marketing", ["organization_name"] = "Acme Industries", ["domain_name"] = "acme.com", ["email"] = "hello@acme.com", ["phone"] = "+1-415-555-0100" })],
                0.90, 0.75),
            CancellationToken.None);
        Assert.Equal(1, mktResult.AutoMatches);

        // DIFFERENT name + different domain but SAME email → auto-match (email identifier).
        var finBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, DateTimeOffset.UtcNow, CancellationToken.None);
        var finResult = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id,
                finBatch.Id,
                [Record(project.Id, source.Id, finBatch.Id, "fin-030", new() { ["source"] = "Finance", ["organization_name"] = "Globex International", ["domain_name"] = "globex-intl.com", ["email"] = "ap@globex.com", ["phone"] = "+1-212-555-0188" })],
                0.90, 0.75),
            CancellationToken.None);
        Assert.Equal(1, finResult.AutoMatches);

        // Final shape: {acme.com x2}, {globex x2}, {initech}, {web-020 singleton} = 4 clusters.
        // Two review tasks:
        //   review_threshold       – web record (Zeta Industries, no domain) review-matched the
        //                             Acme cluster on org-name similarity alone.
        //   cluster_merge_suggestion – mkt record auto-joined Acme (domain) AND review-matched the
        //                             web-020 cluster (org-name similarity) → weak-bridge suggestion.
        var clusters = await store.ListClustersAsync(project.Id, CancellationToken.None);
        Assert.Equal(4, clusters.Count);
        Assert.Equal(2, clusters.Count(c => c.MemberEntityRecordIds.Count == 2));
        var reviews = await store.ListReviewTasksAsync(project.Id, CancellationToken.None);
        Assert.Equal(2, reviews.Count);
        Assert.Contains(reviews, r => r.Reason == "review_threshold");
        Assert.Contains(reviews, r => r.Reason == "cluster_merge_suggestion");
    }

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }
}
