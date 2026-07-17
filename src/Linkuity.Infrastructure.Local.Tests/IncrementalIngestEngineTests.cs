using Linkuity.Core.Models;
using Linkuity.Infrastructure.Local;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;

namespace Linkuity.Infrastructure.Local.Tests;

public class IncrementalIngestEngineTests
{
    private static FileMetadataStore NewStore(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), "linkuity-eng-" + Guid.NewGuid().ToString("N") + ".json");
        return new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = path });
    }

    private static EntityRecord Record(Guid projectId, Guid sourceId, Guid batchId, string srid, Dictionary<string, string> fields, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        SourceId = sourceId,
        IngestBatchId = batchId,
        SourceRecordId = srid,
        Fields = fields,
        CreatedAt = at
    };

    private static async Task<(Guid ProjectId, Guid SourceId)> SeedAsync(FileMetadataStore store, Dictionary<string, string> existing)
    {
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("MDM", "person", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now, CancellationToken.None);
        var rec = Record(project.Id, source.Id, batch.Id, "existing-1", existing, now);
        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata(
                [rec], [],
                [new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [rec.Id], CreatedAt = now }],
                [], []),
            CancellationToken.None);
        return (project.Id, source.Id);
    }

    [Fact]
    public async Task SharedEmail_AutoMatches()
    {
        var store = NewStore(out _);
        var (projectId, sourceId) = await SeedAsync(store, new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "alice@example.com", ["name"] = "Alice" });
        var batch = await store.CreateIngestBatchAsync(projectId, sourceId, null, 1, DateTimeOffset.UtcNow, CancellationToken.None);
        var incoming = Record(projectId, sourceId, batch.Id, "in-1",
            new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "alice@example.com", ["name"] = "Alice Verified" }, DateTimeOffset.UtcNow);

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(projectId, sourceId, batch.Id, [incoming], 0.90, 0.75), CancellationToken.None);

        Assert.Equal(1, result.AutoMatches);
        var clusters = await store.ListClustersAsync(projectId, CancellationToken.None);
        Assert.Single(clusters); // joined the existing cluster, no new singleton
    }

    [Fact]
    public async Task FullyDisjointRecord_NoMatch_NoEdgeNoReview()
    {
        var store = NewStore(out _);
        var (projectId, sourceId) = await SeedAsync(store, new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "a@x.com", ["last_name"] = "Jones" });
        var batch = await store.CreateIngestBatchAsync(projectId, sourceId, null, 1, DateTimeOffset.UtcNow, CancellationToken.None);
        var incoming = Record(projectId, sourceId, batch.Id, "in-1",
            new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "b@y.com", ["last_name"] = "Smith" }, DateTimeOffset.UtcNow);

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(projectId, sourceId, batch.Id, [incoming], 0.90, 0.75), CancellationToken.None);

        Assert.Equal(0, result.AutoMatches);
        Assert.Equal(0, result.ReviewTasks);
        Assert.Equal(1, result.SingletonClusters);
    }

    [Fact]
    public async Task FuzzyTypoOnName_WithSharedDob_AutoMatches()
    {
        var store = NewStore(out _);
        var (projectId, sourceId) = await SeedAsync(store, new Dictionary<string, string>
        { ["source"] = "CRM", ["first_name"] = "Jonathon", ["last_name"] = "Smith", ["date_of_birth"] = "1990-01-02" });
        var batch = await store.CreateIngestBatchAsync(projectId, sourceId, null, 1, DateTimeOffset.UtcNow, CancellationToken.None);
        var incoming = Record(projectId, sourceId, batch.Id, "in-1", new Dictionary<string, string>
        { ["source"] = "CRM", ["first_name"] = "Johnathon", ["last_name"] = "Smith", ["date_of_birth"] = "1990-01-02" }, DateTimeOffset.UtcNow);

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(projectId, sourceId, batch.Id, [incoming], 0.90, 0.75), CancellationToken.None);

        Assert.Equal(1, result.AutoMatches); // fuzzy first-name + exact last-name + exact DOB
    }

    [Fact]
    public async Task FuzzyNameVariant_WithSharedDob_AutoMatchesViaIdentifier()
    {
        var store = NewStore(out _);
        var (projectId, sourceId) = await SeedAsync(store, new Dictionary<string, string>
        { ["source"] = "CRM", ["first_name"] = "Catherine", ["last_name"] = "Smith", ["date_of_birth"] = "1985-06-01" });
        var batch = await store.CreateIngestBatchAsync(projectId, sourceId, null, 1, DateTimeOffset.UtcNow, CancellationToken.None);
        var incoming = Record(projectId, sourceId, batch.Id, "in-1", new Dictionary<string, string>
        { ["source"] = "CRM", ["first_name"] = "Katherine", ["last_name"] = "Smyth", ["date_of_birth"] = "1985-06-01" }, DateTimeOffset.UtcNow);

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(projectId, sourceId, batch.Id, [incoming], 0.90, 0.75), CancellationToken.None);

        // Retrieval is via the shared DOB (exact-value key); the DOB identifier match drives
        // the 0.98 floor -> auto. The name variants (Catherine/Katherine, Smith/Smyth) add
        // fuzzy similarity but are NOT the retrieval path. Genuine phonetic-only retrieval is
        // covered by PhoneticBlockingOnly_SmithSmyth_RetrievedAndScored.
        Assert.Equal(1, result.AutoMatches);
    }

    [Fact]
    public async Task WeightedDisambiguation_SharedCommonSurnameButConflictingIdentifiers_DoesNotAutoMatch()
    {
        var store = NewStore(out _);
        var (projectId, sourceId) = await SeedAsync(store, new Dictionary<string, string>
        { ["source"] = "CRM", ["first_name"] = "Alice", ["last_name"] = "Smith", ["email"] = "alice@example.com", ["date_of_birth"] = "1980-01-01" });
        var batch = await store.CreateIngestBatchAsync(projectId, sourceId, null, 1, DateTimeOffset.UtcNow, CancellationToken.None);
        var incoming = Record(projectId, sourceId, batch.Id, "in-1", new Dictionary<string, string>
        { ["source"] = "CRM", ["first_name"] = "Bob", ["last_name"] = "Smith", ["email"] = "bob@other.com", ["date_of_birth"] = "1975-12-31" }, DateTimeOffset.UtcNow);

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(projectId, sourceId, batch.Id, [incoming], 0.90, 0.75), CancellationToken.None);

        Assert.Equal(0, result.AutoMatches); // weighting: conflicting email + first name + DOB outweighs a common surname
        // The pair's real weighted similarity falls below the review-floor gate (a shared surname
        // plus conflicting email/DOB/first-name is not real evidence), so it does NOT get promoted
        // to the review band either: result.ReviewTasks is 0 (NoMatch).
        Assert.Equal(0, result.ReviewTasks);
    }

    /// <summary>
    /// Genuine phonetic-retrieval test: no first name, DOB, email, or phone between the two
    /// records, so token-name blocking cannot retrieve them (Smith → "name:smith" vs
    /// Smyth → "name:smyth" are different tokens). Only the phonetic blocking strategy
    /// (Double Metaphone) can produce a shared key: both "Smith" and "Smyth" encode to
    /// primary "SM0", so the phonetic-enabled profile retrieves the candidate. last_name is
    /// the only comparable field ("Smith"/"Smyth" fuzzy 0.80), so that IS the weighted score —
    /// it legitimately clears the review-floor gate on its own (no other field dilutes it),
    /// landing the pair in the review band.
    /// </summary>
    [Fact]
    public async Task PhoneticBlockingOnly_SmithSmyth_RetrievedAndScored()
    {
        var path = Path.Combine(Path.GetTempPath(), "linkuity-eng-" + Guid.NewGuid().ToString("N") + ".json");
        var provider = new DefaultMatchingProfileProvider([PhoneticEnabledPersonProfile()]);
        var store = new FileMetadataStore(
            new FileMetadataStoreOptions { DatabasePath = path },
            MatchingDefaults.CreateEngine(),
            provider,
            indexedRetrieval: null);

        var (projectId, sourceId) = await SeedAsync(store, new Dictionary<string, string>
        { ["source"] = "CRM", ["last_name"] = "Smith" });
        var batch = await store.CreateIngestBatchAsync(projectId, sourceId, null, 1, DateTimeOffset.UtcNow, CancellationToken.None);
        var incoming = Record(projectId, sourceId, batch.Id, "in-1", new Dictionary<string, string>
        { ["source"] = "CRM", ["last_name"] = "Smyth" }, DateTimeOffset.UtcNow);

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(projectId, sourceId, batch.Id, [incoming], 0.90, 0.75), CancellationToken.None);

        // Both "Smith" and "Smyth" encode to Double Metaphone primary "SM0".
        // With token-name blocking only they would NOT be retrieved (different tokens).
        // Adding "phonetic" to BlockingStrategies enables retrieval; the real fuzzy similarity
        // (0.80, no other field dilutes it) clears the review-floor gate.
        Assert.True(result.AutoMatches + result.ReviewTasks >= 1);
    }

    private static MatchingProfile PhoneticEnabledPersonProfile()
    {
        var baseProfile = DefaultMatchingProfileProvider.CreatePersonProfile();
        return new MatchingProfile
        {
            ContentType = baseProfile.ContentType,
            Fields = baseProfile.Fields,
            NormalizationStrategy = baseProfile.NormalizationStrategy,
            BlockingStrategies = ["exact-value", "token-name", "phonetic"],
            CandidateRetrievalStrategy = baseProfile.CandidateRetrievalStrategy,
            SimilarityStrategy = baseProfile.SimilarityStrategy,
            ScoringStrategy = baseProfile.ScoringStrategy,
            DecisionStrategy = baseProfile.DecisionStrategy,
            ClusteringStrategy = baseProfile.ClusteringStrategy,
            AutoMatchThreshold = baseProfile.AutoMatchThreshold,
            ReviewThreshold = baseProfile.ReviewThreshold,
            ReviewFloorGate = baseProfile.ReviewFloorGate
        };
    }
}
