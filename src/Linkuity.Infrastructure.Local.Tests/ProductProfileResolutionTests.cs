using Linkuity.Core.Models;
using Linkuity.Infrastructure.Local;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Profiles.Configuration;

namespace Linkuity.Infrastructure.Local.Tests;

public sealed class ProductProfileResolutionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"linkuity-prod-{Guid.NewGuid():N}.json");

    private const string ProductProfileJson = """
    {
      "contentType": "product",
      "fields": [
        { "name": "source",       "semanticType": "SourceIdentifier", "roles": [] },
        { "name": "sku",          "semanticType": "Sku",          "roles": ["Searchable","Matchable","Blocking","Identifier"], "similarityEvaluator": "exact", "weight": 3.0 },
        { "name": "gtin",         "semanticType": "Gtin",         "roles": ["Matchable","Blocking","Identifier"],              "similarityEvaluator": "exact", "weight": 3.0 },
        { "name": "product_name", "semanticType": "ProductName",  "roles": ["Searchable","Matchable","Blocking"],              "similarityEvaluator": "fuzzy", "weight": 2.0 },
        { "name": "brand",        "semanticType": "OrganizationName", "roles": ["Searchable","Matchable"],                     "similarityEvaluator": "fuzzy", "weight": 1.0 }
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

    private FileMetadataStore NewStore()
    {
        var product = new MatchingProfileConfigLoader().LoadFromJson(ProductProfileJson, MatchingDefaults.CreateRegistry());
        var provider = new DefaultMatchingProfileProvider([DefaultMatchingProfileProvider.CreatePersonProfile(), product]);
        return new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = _path }, engine: null, provider, indexedRetrieval: null);
    }

    private static EntityRecord Record(Guid p, Guid s, Guid b, string id, Dictionary<string, string> fields) => new()
    {
        Id = Guid.NewGuid(), ProjectId = p, SourceId = s, IngestBatchId = b,
        SourceRecordId = id, Fields = fields, BlockingKeys = [], CreatedAt = DateTimeOffset.UtcNow
    };

    private static async Task<IncrementalIngestResult> IngestAsync(FileMetadataStore store, Guid p, Guid s, params (string Id, Dictionary<string, string> Fields)[] recs)
    {
        var batch = await store.CreateIngestBatchAsync(p, s, null, recs.Length, DateTimeOffset.UtcNow, CancellationToken.None);
        var records = recs.Select(r => Record(p, s, batch.Id, r.Id, r.Fields)).ToList();
        return await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(p, s, batch.Id, records, 0.90, 0.75), CancellationToken.None);
    }

    [Fact]
    public async Task ProductDataset_ResolvesEndToEnd_WithConfigOnlyProfile()
    {
        var store = NewStore();
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Product MDM", "product", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "Import", now, CancellationToken.None);

        await IngestAsync(store, project.Id, source.Id,
            ("cat-001", new() { ["source"] = "Catalog", ["sku"] = "ALPHA-100", ["gtin"] = "00012345600012", ["product_name"] = "Widget Alpha", ["brand"] = "Acme" }),
            ("cat-002", new() { ["source"] = "Catalog", ["sku"] = "BETA-200", ["gtin"] = "00012345600029", ["product_name"] = "Gadget Beta", ["brand"] = "Globex" }),
            ("cat-003", new() { ["source"] = "Catalog", ["sku"] = "GAMMA-300", ["gtin"] = "00012345600036", ["product_name"] = "Sprocket Gamma", ["brand"] = "Initech" }));
        Assert.Equal(3, (await store.ListClustersAsync(project.Id, CancellationToken.None)).Count);

        // Same SKU, second source, different name -> auto on the SKU identifier.
        var skuResult = await IngestAsync(store, project.Id, source.Id,
            ("shop-010", new() { ["source"] = "Shop", ["sku"] = "ALPHA-100", ["gtin"] = "00099999900010", ["product_name"] = "Widget Alpha 2024", ["brand"] = "Acme" }));
        Assert.Equal(1, skuResult.AutoMatches);
        Assert.Equal(3, (await store.ListClustersAsync(project.Id, CancellationToken.None)).Count);

        // GTIN matches cat-001 while SKU differs -> auto on the GTIN identifier.
        var gtinResult = await IngestAsync(store, project.Id, source.Id,
            ("supp-020", new() { ["source"] = "Supplier", ["sku"] = "ALPHA-100-EU", ["gtin"] = "00012345600012", ["product_name"] = "Widget Alpha EU", ["brand"] = "Acme" }));
        Assert.Equal(1, gtinResult.AutoMatches);
        Assert.Equal(3, (await store.ListClustersAsync(project.Id, CancellationToken.None)).Count);

        // Exact product_name match, no SKU/GTIN captured for this listing (aggregator scrape) and a
        // clearly different brand -> product_name (2.0, exact 1.0) + brand (1.0, "Aftermarket"/"Acme"
        // fuzzy 0.50) over total weight 3.0 = 0.83, which clears the review-floor gate with a
        // comfortable margin on its own real evidence (no identifier fields are even compared here)
        // -> review, separate cluster.
        var nameResult = await IngestAsync(store, project.Id, source.Id,
            ("web-030", new() { ["source"] = "Web", ["product_name"] = "Widget Alpha", ["brand"] = "Aftermarket" }));
        Assert.Equal(0, nameResult.AutoMatches);
        Assert.Equal(1, nameResult.ReviewTasks);

        var clusters = await store.ListClustersAsync(project.Id, CancellationToken.None);
        Assert.Equal(4, clusters.Count);
        Assert.Equal(1, clusters.Count(c => c.MemberEntityRecordIds.Count == 3)); // cat-001 + shop-010 + supp-020
        Assert.Single(await store.ListReviewTasksAsync(project.Id, CancellationToken.None));
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
