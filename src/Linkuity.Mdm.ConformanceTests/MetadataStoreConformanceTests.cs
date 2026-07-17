using Linkuity.Core.Interfaces;
using Linkuity.Core.Models;

namespace Linkuity.Mdm.ConformanceTests;

/// <summary>
/// Cross-backend conformance suite. Every behavioral [Fact] lives here; backend-specific
/// wiring is in the concrete subclass (e.g. <see cref="FileMetadataStoreConformanceTests"/>).
/// xUnit instantiates the class once per [Fact], so <see cref="CreateStoreAsync"/> returns
/// a fresh empty store for each test.
/// </summary>
public abstract class MetadataStoreConformanceTests
{
    /// <summary>Returns a fresh, empty, fully-wired store for a single test run.</summary>
    protected abstract Task<IMetadataStore> CreateStoreAsync();

    /// <summary>
    /// Override in backend-specific subclasses to skip when the backend is unavailable
    /// (e.g. Docker not present for Postgres). The default no-op means File-backed facts always run.
    /// </summary>
    protected virtual void SkipIfUnavailable() { }

    /// <summary>Creates a project + a single CRM source and returns their ids.</summary>
    protected static async Task<(Guid ProjectId, Guid SourceId)> SeedProjectAsync(
        IMetadataStore store, string contentType = "person")
    {
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("MDM", contentType, null, now);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now);
        return (project.Id, source.Id);
    }

    private static EntityRecord MakeRecord(
        Guid projectId, Guid sourceId, Guid batchId,
        string srid, Dictionary<string, string> fields, DateTimeOffset at,
        IReadOnlyList<string>? blockingKeys = null)
        => new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            SourceId = sourceId,
            IngestBatchId = batchId,
            SourceRecordId = srid,
            Fields = fields,
            BlockingKeys = blockingKeys ?? [],
            CreatedAt = at
        };

    // ── Fact 1 ──────────────────────────────────────────────────────────────────────────
    // Lifted from: FileMetadataStoreTests.SaveCompletedBatchAsync_PersistsMetadataAcrossStoreInstances
    [SkippableFact]
    public async Task Project_Source_Batch_RoundTrip()
    {
        SkipIfUnavailable();
        var store = await CreateStoreAsync();
        var now = DateTimeOffset.UtcNow;

        var project = await store.CreateProjectAsync("Customer MDM", "person", null, now);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 2, now);

        var left = MakeRecord(project.Id, source.Id, batch.Id, "crm-001",
            new() { ["email"] = "alice@example.com" }, now);
        var right = MakeRecord(project.Id, source.Id, batch.Id, "mkt-001",
            new() { ["email"] = "alice@example.com" }, now);
        var clusterId = Guid.NewGuid();
        var goldenRecordId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata(
                [left, right],
                [
                    new MatchEdge
                    {
                        Id = Guid.NewGuid(),
                        ProjectId = project.Id,
                        IngestBatchId = batch.Id,
                        LeftEntityRecordId = left.Id,
                        RightEntityRecordId = right.Id,
                        Score = 0.99,
                        Method = "batch",
                        CreatedAt = now
                    }
                ],
                [
                    new Cluster
                    {
                        Id = clusterId,
                        ProjectId = project.Id,
                        MemberEntityRecordIds = [left.Id, right.Id],
                        CreatedAt = now
                    }
                ],
                [
                    new GoldenRecord
                    {
                        Id = goldenRecordId,
                        ProjectId = project.Id,
                        ClusterId = clusterId,
                        CurrentVersionId = versionId,
                        Fields = new Dictionary<string, string> { ["email"] = "alice@example.com" },
                        UpdatedAt = now
                    }
                ],
                [
                    new GoldenRecordVersion
                    {
                        Id = versionId,
                        GoldenRecordId = goldenRecordId,
                        ProjectId = project.Id,
                        ClusterId = clusterId,
                        IngestBatchId = batch.Id,
                        VersionNumber = 1,
                        Fields = new Dictionary<string, string> { ["email"] = "alice@example.com" },
                        CreatedAt = now
                    }
                ]));

        Assert.Equal("Customer MDM", Assert.Single(await store.ListProjectsAsync()).Name);
        Assert.Equal("CRM", Assert.Single(await store.ListSourcesAsync(project.Id)).Name);
        Assert.Equal(batch.Id, Assert.Single(await store.ListIngestBatchesAsync(project.Id)).Id);
        Assert.Equal(2, (await store.ListEntityRecordsAsync(project.Id)).Count);
        Assert.Single(await store.ListMatchEdgesAsync(project.Id));
        Assert.Equal(2, Assert.Single(await store.ListClustersAsync(project.Id)).MemberEntityRecordIds.Count);
        Assert.Equal(versionId, Assert.Single(await store.ListGoldenRecordsAsync(project.Id)).CurrentVersionId);
        Assert.Equal(batch.Id, Assert.Single(await store.ListGoldenRecordVersionsAsync(project.Id)).IngestBatchId);
    }

    // ── Fact 2 ──────────────────────────────────────────────────────────────────────────
    // Lifted from: FileMetadataStoreTests.CreateProjectAsync_WhenNameAlreadyExists_Throws
    [SkippableFact]
    public async Task CreateProject_DuplicateName_Throws()
    {
        SkipIfUnavailable();
        var store = await CreateStoreAsync();
        await store.CreateProjectAsync("Customer MDM", "person", null, DateTimeOffset.UtcNow);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateProjectAsync("customer mdm", "person", null, DateTimeOffset.UtcNow));

        Assert.Contains("Project already exists", ex.Message);
    }

    // ── Fact 3 ──────────────────────────────────────────────────────────────────────────
    // Lifted from: FileMetadataStoreTests.CreateSourceAsync_WhenProjectIsMissing_Throws
    [SkippableFact]
    public async Task CreateSource_MissingProject_Throws()
    {
        SkipIfUnavailable();
        var store = await CreateStoreAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateSourceAsync(Guid.NewGuid(), "CRM", DateTimeOffset.UtcNow));

        Assert.Contains("Project not found", ex.Message);
    }

    // ── Fact 4 ──────────────────────────────────────────────────────────────────────────
    // Lifted from: FileMetadataStoreTests.SaveCompletedBatchAsync_WhenBatchIsMissing_ThrowsWithoutWritingOrphans
    [SkippableFact]
    public async Task SaveCompletedBatch_MissingBatch_ThrowsAndLeavesNoOrphans()
    {
        SkipIfUnavailable();
        var store = await CreateStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Customer MDM", "person", null, now);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now);
        var missingBatchId = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveCompletedBatchAsync(
                new CompletedBatchMetadata(
                    [
                        new EntityRecord
                        {
                            Id = Guid.NewGuid(),
                            ProjectId = project.Id,
                            SourceId = source.Id,
                            IngestBatchId = missingBatchId,
                            SourceRecordId = "crm-001",
                            Fields = new Dictionary<string, string> { ["email"] = "alice@example.com" },
                            CreatedAt = now
                        }
                    ],
                    [], [], [], [])));

        Assert.Contains("Ingest batch not found", ex.Message);
        Assert.Empty(await store.ListEntityRecordsAsync(project.Id));
    }

    // ── Fact 5 ──────────────────────────────────────────────────────────────────────────
    // Lifted from: FileMetadataStoreTests.SaveCompletedBatchAsync_WhenSourceRecordAlreadyExists_ThrowsWithoutDuplicatingFallbackState
    [SkippableFact]
    public async Task SaveCompletedBatch_ExistingSourceRecord_ThrowsAndLeavesNoOrphans()
    {
        SkipIfUnavailable();
        var store = await CreateStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Customer MDM", "person", null, now);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now);
        var firstBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now);
        var duplicateBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now.AddMinutes(1));

        var firstRecord = MakeRecord(project.Id, source.Id, firstBatch.Id, "crm-dup",
            new() { ["email"] = "dup@example.com" }, now, ["email:dup@example.com"]);
        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata(
                [firstRecord], [],
                [new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [firstRecord.Id], CreatedAt = now }],
                [], []));

        var duplicate = MakeRecord(project.Id, source.Id, duplicateBatch.Id, "crm-dup",
            new() { ["email"] = "dup@example.com" }, now.AddMinutes(1), ["email:dup@example.com"]);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveCompletedBatchAsync(
                new CompletedBatchMetadata(
                    [duplicate], [],
                    [new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [duplicate.Id], CreatedAt = now.AddMinutes(1) }],
                    [], [])));

        Assert.Contains("Entity record already exists", ex.Message);
        Assert.Single(await store.ListEntityRecordsAsync(project.Id));
    }

    // ── Fact 6 ──────────────────────────────────────────────────────────────────────────
    // Lifted from: FileMetadataStoreTests.SaveCompletedBatchAsync_UsesProjectMergePolicyInsteadOfImportedGoldenFields
    [SkippableFact]
    public async Task SaveCompletedBatch_WithMergePolicy_PrioritizesSourceByRank()
    {
        SkipIfUnavailable();
        var store = await CreateStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync(
            "Customer MDM", "person",
            new MergeConfiguration
            {
                MergeFields =
                [
                    new MergeField { FieldName = "email", SourcePriority = ["CRM", "Marketing"] }
                ]
            },
            now);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 2, now);
        var crm = MakeRecord(project.Id, source.Id, batch.Id, "crm-001",
            new Dictionary<string, string>
            {
                ["id"] = "crm-001", ["source"] = "CRM",
                ["email"] = "crm@example.com", ["name"] = "Alice CRM"
            },
            now, ["email:alice"]);
        var marketing = MakeRecord(project.Id, source.Id, batch.Id, "mkt-001",
            new Dictionary<string, string>
            {
                ["id"] = "mkt-001", ["source"] = "Marketing",
                ["email"] = "marketing@example.com", ["name"] = "Alice Marketing"
            },
            now, ["email:alice"]);
        var clusterId = Guid.NewGuid();

        // The imported golden deliberately has the Marketing email — the store must override
        // it with the CRM value using the merge policy.
        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata(
                [crm, marketing],
                [],
                [new Cluster { Id = clusterId, ProjectId = project.Id, MemberEntityRecordIds = [crm.Id, marketing.Id], CreatedAt = now }],
                [
                    new GoldenRecord
                    {
                        Id = Guid.NewGuid(), ProjectId = project.Id, ClusterId = clusterId,
                        CurrentVersionId = Guid.NewGuid(),
                        Fields = new Dictionary<string, string> { ["email"] = "marketing@example.com" },
                        UpdatedAt = now
                    }
                ],
                []));

        var golden = Assert.Single(await store.ListGoldenRecordsAsync(project.Id));
        Assert.Equal("crm@example.com", golden.Fields["email"]);
        Assert.Equal("crm@example.com", Assert.Single(await store.ListGoldenRecordVersionsAsync(project.Id)).Fields["email"]);
    }

    // ── Fact 7 ──────────────────────────────────────────────────────────────────────────
    // Lifted from: IncrementalIngestEngineTests.SharedEmail_AutoMatches +
    //              FileMetadataStoreTests.SaveIncrementalIngestAsync_AutoMatchesExistingClusterAndCreatesGoldenVersion
    [SkippableFact]
    public async Task Incremental_SharedEmail_AutoMatches_JoinsClusterAndCreatesGoldenVersion()
    {
        SkipIfUnavailable();
        var store = await CreateStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Customer MDM", "person", null, now);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now);
        var initialBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now);

        // No explicit BlockingKeys: SaveCompletedBatchAsync derives them via the engine so
        // the Lucene index contains the same normalized keys the incoming record's query uses.
        var existing = MakeRecord(project.Id, source.Id, initialBatch.Id, "crm-001",
            new() { ["source"] = "CRM", ["email"] = "alice@example.com", ["name"] = "Alice" },
            now);
        var clusterId = Guid.NewGuid();
        var goldenRecordId = Guid.NewGuid();
        var initialVersionId = Guid.NewGuid();
        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata(
                [existing], [],
                [new Cluster { Id = clusterId, ProjectId = project.Id, MemberEntityRecordIds = [existing.Id], CreatedAt = now }],
                [
                    new GoldenRecord
                    {
                        Id = goldenRecordId, ProjectId = project.Id, ClusterId = clusterId,
                        CurrentVersionId = initialVersionId,
                        Fields = new Dictionary<string, string> { ["email"] = "alice@example.com", ["name"] = "Alice" },
                        UpdatedAt = now
                    }
                ],
                [
                    new GoldenRecordVersion
                    {
                        Id = initialVersionId, GoldenRecordId = goldenRecordId,
                        ProjectId = project.Id, ClusterId = clusterId, IngestBatchId = initialBatch.Id,
                        VersionNumber = 1,
                        Fields = new Dictionary<string, string> { ["email"] = "alice@example.com", ["name"] = "Alice" },
                        CreatedAt = now
                    }
                ]));

        var incrementalBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1));
        var incoming = MakeRecord(project.Id, source.Id, incrementalBatch.Id, "web-001",
            new() { ["source"] = "CRM", ["email"] = "alice@example.com", ["name"] = "Alice Verified" },
            now.AddMinutes(1));

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, incrementalBatch.Id, [incoming], 0.90, 0.75));

        Assert.Equal(1, result.RecordsAdded);
        Assert.Equal(1, result.AutoMatches);
        Assert.Equal(0, result.ReviewTasks);
        Assert.Equal(0, result.SingletonClusters);
        Assert.Equal(1, result.GoldenRecordVersionsCreated);

        Assert.Equal(2, (await store.ListEntityRecordsAsync(project.Id)).Count);
        var cluster = Assert.Single(await store.ListClustersAsync(project.Id));
        Assert.Equal(clusterId, cluster.Id);
        Assert.Contains(incoming.Id, cluster.MemberEntityRecordIds);
        Assert.Single(await store.ListMatchEdgesAsync(project.Id));
        var golden = Assert.Single(await store.ListGoldenRecordsAsync(project.Id));
        Assert.Equal("Alice Verified", golden.Fields["name"]);
        var versions = await store.ListGoldenRecordVersionsAsync(project.Id);
        Assert.Equal(2, versions.Count);
        Assert.Contains(versions, v => v.IngestBatchId == incrementalBatch.Id);
    }

    // ── Fact 8 ──────────────────────────────────────────────────────────────────────────
    // Lifted from: WithinBatchResolutionTests.TwoNetNewInBatchDuplicates_AutoBand_FormOneCluster
    [SkippableFact]
    public async Task Incremental_WithinBatch_TwoNetNewDuplicates_CoCluster()
    {
        SkipIfUnavailable();
        var store = await CreateStoreAsync();
        var (projectId, sourceId) = await SeedProjectAsync(store);
        var batch = await store.CreateIngestBatchAsync(projectId, sourceId, null, 2, DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var a = MakeRecord(projectId, sourceId, batch.Id, "in-a",
            new() { ["source"] = "CRM", ["email"] = "maria@x.com", ["name"] = "Maria Garcia" }, now);
        var b = MakeRecord(projectId, sourceId, batch.Id, "in-b",
            new() { ["source"] = "CRM", ["email"] = "maria@x.com", ["name"] = "Maria Garcia" }, now);

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(projectId, sourceId, batch.Id, [a, b], 0.90, 0.75));

        var clusters = await store.ListClustersAsync(projectId);
        Assert.Single(clusters);
        Assert.Equal(2, clusters[0].MemberEntityRecordIds.Count);
        Assert.True(result.AutoMatches >= 1);
    }

    // ── Fact 9 ──────────────────────────────────────────────────────────────────────────
    // Lifted from: FileMetadataStoreTests.SaveIncrementalIngestAsync_WhenAutoMatchesMultipleExistingClusters_MergesBridgedClusters
    // (survivor=oldest CreatedAt wins, loser absorbed, one ClusterMergeEvent)
    [SkippableFact]
    public async Task Incremental_BridgeMerge_SurvivorOldest_MergeEventRecorded()
    {
        SkipIfUnavailable();
        var store = await CreateStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Customer MDM", "person", null, now);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now);
        var initialBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 2, now);

        // Two records sharing email in separate clusters; leftCluster is older.
        // No explicit BlockingKeys so SaveCompletedBatchAsync derives engine-normalized keys
        // that the Lucene index query will correctly match.
        var left = MakeRecord(project.Id, source.Id, initialBatch.Id, "crm-left",
            new() { ["source"] = "CRM", ["email"] = "shared@example.com", ["name"] = "Left Person" },
            now);
        var right = MakeRecord(project.Id, source.Id, initialBatch.Id, "crm-right",
            new() { ["source"] = "CRM", ["email"] = "shared@example.com", ["name"] = "Right Person" },
            now);
        var leftCluster = Guid.NewGuid();
        var rightCluster = Guid.NewGuid();
        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata(
                [left, right], [],
                [
                    new Cluster { Id = leftCluster, ProjectId = project.Id, MemberEntityRecordIds = [left.Id], CreatedAt = now },
                    new Cluster { Id = rightCluster, ProjectId = project.Id, MemberEntityRecordIds = [right.Id], CreatedAt = now.AddSeconds(1) }
                ],
                [], []));

        var incrementalBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1));
        var incoming = MakeRecord(project.Id, source.Id, incrementalBatch.Id, "web-shared",
            new() { ["source"] = "CRM", ["email"] = "shared@example.com", ["name"] = "Shared Person" },
            now.AddMinutes(1));

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, incrementalBatch.Id, [incoming], 0.90, 0.75));

        Assert.Equal(0, result.ReviewTasks);
        Assert.True(result.AutoMatches >= 1);

        var clusters = await store.ListClustersAsync(project.Id);
        Assert.Single(clusters);                                   // merged into one survivor
        Assert.Equal(leftCluster, clusters[0].Id);                // oldest CreatedAt wins
        Assert.Equal(3, clusters[0].MemberEntityRecordIds.Count);  // left, right, incoming

        var merges = await store.ListClusterMergeEventsAsync(project.Id);
        Assert.Single(merges);
        Assert.Equal(leftCluster, merges[0].SurvivorClusterId);
        Assert.Empty(await store.ListReviewTasksAsync(project.Id));
    }

    // ── Fact 10 ─────────────────────────────────────────────────────────────────────────
    // Lifted from: WithinBatchResolutionTests.AutoBridge_MergesClusters_WithSurvivorTombstoneAndMergeEvent
    // Verifies: ListClusters and ListGolden exclude the tombstoned/absorbed cluster.
    [SkippableFact]
    public async Task ListClusters_And_ListGolden_ExcludeTombstoned_AfterBridgeMerge()
    {
        SkipIfUnavailable();
        var store = await CreateStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("MDM", "person", null, now);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now);
        var batch0 = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 2, now);

        // C1: email+name; C2: phone+name. They share nothing → two separate clusters.
        // No explicit BlockingKeys: engine-normalized keys must match what the Lucene query uses.
        var r1 = MakeRecord(project.Id, source.Id, batch0.Id, "ex-1",
            new() { ["source"] = "CRM", ["email"] = "jon@acme.com", ["name"] = "Jonathan Smith" },
            now);
        var r2 = MakeRecord(project.Id, source.Id, batch0.Id, "ex-2",
            new() { ["source"] = "CRM", ["phone"] = "555-9876", ["name"] = "J Smith" },
            now);
        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata(
                [r1, r2], [],
                [
                    new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [r1.Id], CreatedAt = now },
                    new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [r2.Id], CreatedAt = now.AddSeconds(1) }
                ],
                [], []));

        // X matches C1 via email AND C2 via phone → bridge-merge.
        var batch1 = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1));
        var x = MakeRecord(project.Id, source.Id, batch1.Id, "in-x",
            new() { ["source"] = "CRM", ["email"] = "jon@acme.com", ["phone"] = "555-9876", ["name"] = "Jonathan Smith" },
            now.AddMinutes(1));

        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, batch1.Id, [x], 0.90, 0.75));

        // After the merge: only the survivor cluster is listed; one golden record.
        var activeClusters = await store.ListClustersAsync(project.Id);
        Assert.Single(activeClusters);                             // loser is tombstoned/excluded
        Assert.Equal(3, activeClusters[0].MemberEntityRecordIds.Count); // r1, r2, x

        var merges = await store.ListClusterMergeEventsAsync(project.Id);
        Assert.Single(merges);
        Assert.Equal(activeClusters[0].Id, merges[0].SurvivorClusterId);
        Assert.Contains(x.Id, merges[0].TriggerRecordIds);
        Assert.Contains(r2.Id, merges[0].AbsorbedMemberEntityRecordIds);

        Assert.Single(await store.ListGoldenRecordsAsync(project.Id));  // loser golden excluded
    }

    // ── Fact 11 ─────────────────────────────────────────────────────────────────────────
    // Lifted from: IncrementalIngestExplainabilityTests.AutoMatchEdge_PersistsDecisionAndBreakdown
    [SkippableFact]
    public async Task Incremental_AutoEdge_PersistsDecisionAndBreakdown()
    {
        SkipIfUnavailable();
        var store = await CreateStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("MDM", "person", null, now);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now);
        var seedBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now);
        var existing = MakeRecord(project.Id, source.Id, seedBatch.Id, "existing-1",
            new() { ["source"] = "CRM", ["email"] = "alice@example.com", ["last_name"] = "Smith" },
            now);
        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata(
                [existing], [],
                [new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [existing.Id], CreatedAt = now }],
                [], []));

        var incBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1));
        var incoming = MakeRecord(project.Id, source.Id, incBatch.Id, "in-1",
            new() { ["source"] = "CRM", ["email"] = "alice@example.com", ["last_name"] = "Smith" },
            now.AddMinutes(1));

        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, incBatch.Id, [incoming], 0.90, 0.75));

        var edges = await store.ListMatchEdgesAsync(project.Id);
        var edge = Assert.Single(edges);
        Assert.Equal("auto", edge.Decision);
        Assert.NotEmpty(edge.Breakdown);
        Assert.Contains(edge.Breakdown, f => f.Signal.Length > 0);
    }

    // ── Fact 12 ─────────────────────────────────────────────────────────────────────────
    // Lifted from: IncrementalIngestExplainabilityTests.ReviewTask_PersistsBreakdown
    // Matching last name plus a real, non-identifier first-name similarity signal (no email on
    // either side): last_name exact (2.0) + first_name "Robert"/"Bob" fuzzy 0.67 (1.0) over total
    // weight 3.0 = 0.89, which legitimately clears the review-floor gate — a genuinely uncertain
    // pair, not a blocking-key-only match.
    [SkippableFact]
    public async Task Incremental_ReviewBand_CreatesOpenReviewTask_WithBreakdown()
    {
        SkipIfUnavailable();
        var store = await CreateStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("MDM", "person", null, now);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now);
        var seedBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now);
        var existing = MakeRecord(project.Id, source.Id, seedBatch.Id, "existing-1",
            new() { ["source"] = "CRM", ["first_name"] = "Robert", ["last_name"] = "Smith" },
            now);
        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata(
                [existing], [],
                [new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [existing.Id], CreatedAt = now }],
                [], []));

        var incBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1));
        var incoming = MakeRecord(project.Id, source.Id, incBatch.Id, "in-1",
            new() { ["source"] = "CRM", ["first_name"] = "Bob", ["last_name"] = "Smith" },
            now.AddMinutes(1));

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, incBatch.Id, [incoming], 0.90, 0.75));

        Assert.True(result.ReviewTasks >= 1);
        var tasks = await store.ListReviewTasksAsync(project.Id);
        Assert.All(tasks, t => Assert.NotEmpty(t.Breakdown));
        // All tasks must have Status="open".
        Assert.All(tasks, t => Assert.Equal("open", t.Status));
    }

    // ── Fact 13 ─────────────────────────────────────────────────────────────────────────
    // Lifted from: FileMetadataStoreTests.SaveIncrementalIngestAsync_WhenIncomingSourceRecordIdsRepeat_Throws
    [SkippableFact]
    public async Task Incremental_DuplicateSourceRecordId_ThrowsAndLeavesNoOrphans()
    {
        SkipIfUnavailable();
        var store = await CreateStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var (projectId, sourceId) = await SeedProjectAsync(store);
        var batch = await store.CreateIngestBatchAsync(projectId, sourceId, null, 2, now);

        var left = MakeRecord(projectId, sourceId, batch.Id, "dup-incoming",
            new() { ["email"] = "left@example.com", ["name"] = "Dup Left" }, now);
        var right = MakeRecord(projectId, sourceId, batch.Id, "dup-incoming",
            new() { ["email"] = "right@example.com", ["name"] = "Dup Right" }, now);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveIncrementalIngestAsync(
                new IncrementalIngestRequest(projectId, sourceId, batch.Id, [left, right], 0.90, 0.75)));

        Assert.Contains("Duplicate source record id", ex.Message);
        Assert.Empty(await store.ListEntityRecordsAsync(projectId));
    }

    // ── Fact 14 ─────────────────────────────────────────────────────────────────────────
    // Lifted from: FileMetadataStoreTests.SaveIncrementalIngestAsync_UsesSameProjectMergePolicyAsCompletedBatch
    // Merge-policy priority is applied consistently in both the full-import and incremental paths.
    [SkippableFact]
    public async Task Incremental_MergePolicy_PriorityConsistentAcrossBatchTypes()
    {
        SkipIfUnavailable();
        var store = await CreateStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync(
            "Customer MDM", "person",
            new MergeConfiguration
            {
                MergeFields =
                [
                    new MergeField { FieldName = "email", SourcePriority = ["CRM", "Marketing", "Web"] }
                ]
            },
            now);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now);
        var initialBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 2, now);

        var crm = MakeRecord(project.Id, source.Id, initialBatch.Id, "crm-001",
            new Dictionary<string, string>
            {
                ["id"] = "crm-001", ["source"] = "CRM",
                ["email"] = "crm@example.com", ["phone"] = "5550100", ["name"] = "Alice CRM"
            },
            now, ["phone:5550100"]);
        var marketing = MakeRecord(project.Id, source.Id, initialBatch.Id, "mkt-001",
            new Dictionary<string, string>
            {
                ["id"] = "mkt-001", ["source"] = "Marketing",
                ["email"] = "marketing@example.com", ["phone"] = "5550100", ["name"] = "Alice Marketing"
            },
            now, ["phone:5550100"]);
        var clusterId = Guid.NewGuid();

        // Full import with empty golden list: store creates golden using merge policy.
        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata(
                [crm, marketing], [],
                [new Cluster { Id = clusterId, ProjectId = project.Id, MemberEntityRecordIds = [crm.Id, marketing.Id], CreatedAt = now }],
                [], []));

        var fullImportEmail = Assert.Single(await store.ListGoldenRecordsAsync(project.Id)).Fields["email"];

        // Incremental: Web joins via shared phone. CRM still wins under merge policy.
        var incrementalBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1));
        var web = MakeRecord(project.Id, source.Id, incrementalBatch.Id, "web-001",
            new Dictionary<string, string>
            {
                ["id"] = "web-001", ["source"] = "Web",
                ["email"] = "web@example.com", ["phone"] = "5550100", ["name"] = "Alice Web"
            },
            now.AddMinutes(1), ["phone:5550100"]);

        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, incrementalBatch.Id, [web], 0.90, 0.75));

        var incrementalEmail = Assert.Single(await store.ListGoldenRecordsAsync(project.Id)).Fields["email"];

        Assert.Equal("crm@example.com", fullImportEmail);
        Assert.Equal(fullImportEmail, incrementalEmail);
    }

    // ── Fact 15 ─────────────────────────────────────────────────────────────────────────
    // Lifted from: WithinBatchResolutionTests.WeakBridge_DoesNotMerge_CreatesClusterMergeSuggestion
    // X auto-joins C1 (email) but only review-band matches C2 (name similarity) →
    // clusters do NOT merge; exactly one cluster_merge_suggestion review task.
    [SkippableFact]
    public async Task Incremental_WeakBridge_DoesNotMerge_CreatesClusterMergeSuggestion()
    {
        SkipIfUnavailable();
        var store = await CreateStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("MDM", "person", null, now);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now);
        var batch0 = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 2, now);

        // C1: has email so X can auto-join via identifier floor; distinct name so C1↔C2 share nothing.
        var r1 = MakeRecord(project.Id, source.Id, batch0.Id, "wb-1",
            new() { ["source"] = "CRM", ["email"] = "jon@acme.com", ["name"] = "Alice Adams" }, now);
        // C2: name-only → can only be review-band linked.
        var r2 = MakeRecord(project.Id, source.Id, batch0.Id, "wb-2",
            new() { ["source"] = "CRM", ["name"] = "Jon Smith" }, now);
        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata(
                [r1, r2], [],
                [
                    new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [r1.Id], CreatedAt = now },
                    new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [r2.Id], CreatedAt = now.AddSeconds(1) }
                ],
                [], []));

        var batch1 = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1));
        var x = MakeRecord(project.Id, source.Id, batch1.Id, "in-x",
            new() { ["source"] = "CRM", ["email"] = "jon@acme.com", ["name"] = "Jonathan Smith" },
            now.AddMinutes(1));

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, batch1.Id, [x], 0.90, 0.75));

        // Exactly 1 auto-match (X→C1 via email) and exactly 1 review task (X↔C2 name-similarity).
        Assert.Equal(1, result.AutoMatches);
        Assert.Equal(1, result.ReviewTasks);

        Assert.Equal(2, (await store.ListClustersAsync(project.Id)).Count); // clusters do not merge
        Assert.Empty(await store.ListClusterMergeEventsAsync(project.Id));
        var reviews = await store.ListReviewTasksAsync(project.Id);
        var suggestion = Assert.Single(reviews, t => t.Reason == "cluster_merge_suggestion");
        Assert.NotNull(suggestion.LeftClusterId);
        Assert.NotNull(suggestion.RightClusterId);
        Assert.NotEqual(suggestion.LeftClusterId, suggestion.RightClusterId);
        Assert.Equal(x.Id, suggestion.NewEntityRecordId);
    }

    // ── Fact 16 ─────────────────────────────────────────────────────────────────────────
    // Lifted from: WithinBatchResolutionTests.Resolution_IsOrderIndependent
    // a~b auto (shared email), b~c auto (shared phone) → transitive closure to one cluster
    // regardless of input order (orderings [0,1,2], [2,1,0], [1,2,0] all tested).
    [SkippableFact]
    public async Task Incremental_Resolution_IsOrderIndependent()
    {
        SkipIfUnavailable();
        int[][] orderings = [[0, 1, 2], [2, 1, 0], [1, 2, 0]];
        foreach (var order in orderings)
        {
            var store = await CreateStoreAsync();
            var (projectId, sourceId) = await SeedProjectAsync(store);
            var batch = await store.CreateIngestBatchAsync(projectId, sourceId, null, 3, DateTimeOffset.UtcNow);
            var now = DateTimeOffset.UtcNow;
            var a = MakeRecord(projectId, sourceId, batch.Id, "a",
                new() { ["source"] = "CRM", ["email"] = "p@x.com", ["name"] = "Pat Lee" }, now);
            var b = MakeRecord(projectId, sourceId, batch.Id, "b",
                new() { ["source"] = "CRM", ["email"] = "p@x.com", ["phone"] = "555-1", ["name"] = "Pat Lee" }, now);
            var c = MakeRecord(projectId, sourceId, batch.Id, "c",
                new() { ["source"] = "CRM", ["phone"] = "555-1", ["name"] = "Patrick Lee" }, now);
            var records = new[] { a, b, c };

            await store.SaveIncrementalIngestAsync(
                new IncrementalIngestRequest(projectId, sourceId, batch.Id,
                    [records[order[0]], records[order[1]], records[order[2]]], 0.90, 0.75));

            var clusters = await store.ListClustersAsync(projectId);
            Assert.Single(clusters);                                  // transitive closure: a, b, c in one cluster
            Assert.Equal(3, clusters[0].MemberEntityRecordIds.Count);
        }
    }

    // ── Fact 17 ─────────────────────────────────────────────────────────────────────────
    // Lifted from: IncrementalIngestEngineTests.FullyDisjointRecord_NoMatch_NoEdgeNoReview
    // A record sharing no keys with any existing record becomes its own singleton cluster.
    [SkippableFact]
    public async Task Incremental_NoMatch_CreatesSingletonCluster()
    {
        SkipIfUnavailable();
        var store = await CreateStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("MDM", "person", null, now);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now);
        var seedBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now);
        var existing = MakeRecord(project.Id, source.Id, seedBatch.Id, "existing-1",
            new() { ["source"] = "CRM", ["email"] = "a@x.com", ["last_name"] = "Jones" }, now);
        await store.SaveCompletedBatchAsync(
            new CompletedBatchMetadata(
                [existing], [],
                [new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [existing.Id], CreatedAt = now }],
                [], []));

        var incBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1));
        var incoming = MakeRecord(project.Id, source.Id, incBatch.Id, "in-1",
            new() { ["source"] = "CRM", ["email"] = "b@y.com", ["last_name"] = "Smith" },
            now.AddMinutes(1));

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, incBatch.Id, [incoming], 0.90, 0.75));

        Assert.Equal(0, result.AutoMatches);
        Assert.Equal(0, result.ReviewTasks);
        Assert.Equal(1, result.SingletonClusters);
        var clusters = await store.ListClustersAsync(project.Id);
        Assert.Equal(2, clusters.Count);
        Assert.Contains(clusters, c => c.MemberEntityRecordIds.Count == 1 && c.MemberEntityRecordIds.Contains(incoming.Id));
    }

    // ── Fact 18 ─────────────────────────────────────────────────────────────────────────
    // A batch created WITHOUT a record count (0) has its stored RecordCount reconciled to the
    // number of records actually ingested by SaveIncrementalIngestAsync.
    [SkippableFact]
    public async Task Incremental_ReconcilesBatchRecordCount_WhenCreatedWithoutCount()
    {
        SkipIfUnavailable();
        var store = await CreateStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var (projectId, sourceId) = await SeedProjectAsync(store);

        // Batch created WITHOUT a record count (0) — mirrors the now-optional CLI --record-count.
        var batch = await store.CreateIngestBatchAsync(projectId, sourceId, null, 0, now);

        var a = MakeRecord(projectId, sourceId, batch.Id, "rc-a",
            new() { ["source"] = "CRM", ["email"] = "a@x.com", ["name"] = "Ann Alpha" }, now);
        var b = MakeRecord(projectId, sourceId, batch.Id, "rc-b",
            new() { ["source"] = "CRM", ["email"] = "b@x.com", ["name"] = "Ben Beta" }, now);
        var c = MakeRecord(projectId, sourceId, batch.Id, "rc-c",
            new() { ["source"] = "CRM", ["email"] = "c@x.com", ["name"] = "Cy Gamma" }, now);

        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(projectId, sourceId, batch.Id, [a, b, c], 0.90, 0.75));

        var stored = Assert.Single(await store.ListIngestBatchesAsync(projectId));
        Assert.Equal(batch.Id, stored.Id);
        Assert.Equal(3, stored.RecordCount);
    }
}
