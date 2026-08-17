using Dapper;
using Linkuity.Core.Models;
using Linkuity.Infrastructure.Lucene;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.TestSupport;

namespace Linkuity.Infrastructure.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresCorrectionTests(SharedPostgresContainer shared) : IAsyncLifetime
{
    private string? _connectionString;
    private PostgresMetadataStore? _store;

    public async Task InitializeAsync()
    {
        if (!shared.IsAvailable)
            return; // tests will self-skip

        _connectionString = await shared.CreateDatabaseAsync(nameof(PostgresCorrectionTests));
        DbUpMigrator.EnsureSchema(_connectionString);

        _store = new PostgresMetadataStore(
            new PostgresMetadataStoreOptions { ConnectionString = _connectionString },
            engine: null,
            profileProvider: null,
            indexedRetrieval: null);
    }

    public Task DisposeAsync() => Task.CompletedTask;

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

    [SkippableFact]
    public async Task ResendWithDifferentValues_AppliesCorrectionInsteadOfThrowing()
    {
        Skip.IfNot(shared.IsAvailable, "Docker not available — skipping Testcontainers test");
        var store = _store!;
        var now = DateTimeOffset.UtcNow;

        var project = await store.CreateProjectAsync("Customer MDM", "person", null, now);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now);
        var original = Record(project.Id, source.Id, batch.Id, "crm-001",
            new() { ["source"] = "CRM", ["email"] = "alice@old.example.com", ["name"] = "Alice" }, now);

        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, batch.Id, [original], 0.90, 0.75));

        var correctionBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1));
        var corrected = Record(project.Id, source.Id, correctionBatch.Id, "crm-001",
            new() { ["source"] = "CRM", ["email"] = "alice@new.example.com", ["name"] = "Alice" }, now.AddMinutes(1));

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, correctionBatch.Id, [corrected], 0.90, 0.75));

        Assert.Equal(1, result.RecordsCorrected);

        var current = Assert.Single(await store.ListEntityRecordsAsync(project.Id));
        Assert.Equal("alice@new.example.com", current.Fields["email"]);

        var events = await store.ListRecordCorrectedEventsAsync(project.Id);
        var evt = Assert.Single(events);
        Assert.Equal("alice@old.example.com", evt.PreviousFields["email"]);
        Assert.Equal("alice@new.example.com", evt.NewFields["email"]);
    }

    [SkippableFact]
    public async Task ResendWithIdenticalValues_IsNoOp()
    {
        Skip.IfNot(shared.IsAvailable, "Docker not available — skipping Testcontainers test");
        var store = _store!;
        var now = DateTimeOffset.UtcNow;

        var project = await store.CreateProjectAsync("Customer MDM", "person", null, now);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now);
        var original = Record(project.Id, source.Id, batch.Id, "crm-001",
            new() { ["source"] = "CRM", ["email"] = "alice@example.com" }, now);

        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, batch.Id, [original], 0.90, 0.75));

        var retryBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1));
        var identicalResend = Record(project.Id, source.Id, retryBatch.Id, "crm-001",
            new() { ["source"] = "CRM", ["email"] = "alice@example.com" }, now.AddMinutes(1));

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, retryBatch.Id, [identicalResend], 0.90, 0.75));

        Assert.Equal(0, result.RecordsCorrected);
        Assert.Equal(0, result.RecordsAdded);
        Assert.Single(await store.ListEntityRecordsAsync(project.Id));
    }

    /// <summary>
    /// The Postgres-specific regression this milestone exists to prevent: without the
    /// cluster_id-clearing fix in PostgresMutationApplier, a corrected-away record's cluster_id
    /// would stay stale, and it would silently reappear as a "member" the next time this cluster's
    /// membership is read from entity_records.cluster_id.
    /// </summary>
    [SkippableFact]
    public async Task Correction_ClearsClusterIdOnDepartingRecord_SurvivorKeepsCorrectMembership()
    {
        Skip.IfNot(shared.IsAvailable, "Docker not available — skipping Testcontainers test");
        var store = _store!;
        var now = DateTimeOffset.UtcNow;

        var project = await store.CreateProjectAsync("Customer MDM", "person", null, now);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now);
        var seedBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 2, now);

        var toCorrect = Record(project.Id, source.Id, seedBatch.Id, "crm-001",
            new() { ["source"] = "CRM", ["email"] = "shared@example.com", ["name"] = "Alice" }, now);
        var survivor = Record(project.Id, source.Id, seedBatch.Id, "crm-002",
            new() { ["source"] = "CRM", ["email"] = "shared@example.com", ["name"] = "Bob" }, now);
        var clusterId = Guid.NewGuid();
        await store.SaveCompletedBatchAsync(new CompletedBatchMetadata(
            [toCorrect, survivor], [],
            [new Cluster { Id = clusterId, ProjectId = project.Id, MemberEntityRecordIds = [toCorrect.Id, survivor.Id], CreatedAt = now }],
            [], []));

        var correctionBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1));
        // Different email — the identifier field — so the corrected record doesn't rejoin the cluster.
        var corrected = Record(project.Id, source.Id, correctionBatch.Id, "crm-001",
            new() { ["source"] = "CRM", ["email"] = "different@example.com", ["name"] = "Alice" }, now.AddMinutes(1));

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, correctionBatch.Id, [corrected], 0.90, 0.75));
        Assert.Equal(1, result.RecordsCorrected);

        // The original cluster still exists (survivor.Id is still a member) — its member count
        // must be exactly 1, not 2. If toCorrect.Id's cluster_id was never cleared, it would
        // silently still count as a member here. (The corrected record, with a non-matching
        // email, forms its own separate fresh singleton cluster via the normal Resolve() path —
        // verified against FileMetadataStore's identical, already-shipped behavior for this exact
        // scenario — so the project ends up with two active clusters total; this assertion targets
        // the original cluster specifically rather than asserting a total count.)
        var clusters = await store.ListClustersAsync(project.Id);
        var activeCluster = Assert.Single(clusters, c => c.Id == clusterId);
        Assert.Equal([survivor.Id], activeCluster.MemberEntityRecordIds);
    }

    [SkippableFact]
    public async Task Correction_ClusterHadOnlyThisMember_ClusterTombstoned()
    {
        Skip.IfNot(shared.IsAvailable, "Docker not available — skipping Testcontainers test");
        var store = _store!;
        var now = DateTimeOffset.UtcNow;

        var project = await store.CreateProjectAsync("Customer MDM", "person", null, now);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now);
        var seedBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now);

        var only = Record(project.Id, source.Id, seedBatch.Id, "crm-001",
            new() { ["source"] = "CRM", ["email"] = "alice@example.com", ["name"] = "Alice" }, now);
        var clusterId = Guid.NewGuid();
        await store.SaveCompletedBatchAsync(new CompletedBatchMetadata(
            [only], [],
            [new Cluster { Id = clusterId, ProjectId = project.Id, MemberEntityRecordIds = [only.Id], CreatedAt = now }],
            [], []));

        var correctionBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1));
        var corrected = Record(project.Id, source.Id, correctionBatch.Id, "crm-001",
            new() { ["source"] = "CRM", ["email"] = "different@example.com", ["name"] = "Alice" }, now.AddMinutes(1));

        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, correctionBatch.Id, [corrected], 0.90, 0.75));

        // Old cluster is gone from the active list (tombstoned); the corrected record formed its
        // own fresh singleton cluster instead.
        var clusters = await store.ListClustersAsync(project.Id);
        Assert.DoesNotContain(clusters, c => c.Id == clusterId);
    }

    [SkippableFact]
    public async Task ResendWithDifferentValues_OnIndexedStore_RemovesSupersededFromIndex()
    {
        Skip.IfNot(shared.IsAvailable, "Docker not available — skipping Testcontainers test");

        var indexDir = Path.Combine(Path.GetTempPath(), "linkuity-pg-correction-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(indexDir);
        try
        {
            using var index = new LuceneCandidateRetrieval(new LuceneCandidateRetrievalOptions { IndexDirectory = indexDir });
            var connectionString = await shared.CreateDatabaseAsync(nameof(ResendWithDifferentValues_OnIndexedStore_RemovesSupersededFromIndex));
            DbUpMigrator.EnsureSchema(connectionString);
            var store = new PostgresMetadataStore(
                new PostgresMetadataStoreOptions { ConnectionString = connectionString },
                engine: null, profileProvider: null, indexedRetrieval: index);
            var engine = MatchingDefaults.CreateEngine();
            var profile = DefaultMatchingProfileProvider.CreatePersonProfile();
            var now = DateTimeOffset.UtcNow;

            var project = await store.CreateProjectAsync("Customer MDM", "person", null, now);
            var source = await store.CreateSourceAsync(project.Id, "CRM", now);
            var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now);
            var original = Record(project.Id, source.Id, batch.Id, "crm-001",
                new() { ["source"] = "CRM", ["email"] = "alice@old.example.com" }, now);
            await store.SaveIncrementalIngestAsync(
                new IncrementalIngestRequest(project.Id, source.Id, batch.Id, [original], 0.90, 0.75));

            // Blocking keys must be generated the same way the store generates them for real
            // incoming records (engine.PrepareForStorage), or the query never builds a search term.
            var queryOldEmail = engine.PrepareForStorage(
                Record(project.Id, source.Id, batch.Id, "probe",
                    new() { ["source"] = "CRM", ["email"] = "alice@old.example.com" }, now.AddMinutes(1)),
                profile);
            // Prove the probe is capable of finding the record before correction, so the later
            // DoesNotContain means "removed", not "this query never matches anything."
            Assert.Contains(index.Retrieve(queryOldEmail, [], profile), c => c.Id == original.Id);

            var correctionBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1));
            var corrected = Record(project.Id, source.Id, correctionBatch.Id, "crm-001",
                new() { ["source"] = "CRM", ["email"] = "alice@new.example.com" }, now.AddMinutes(1));

            var result = await store.SaveIncrementalIngestAsync(
                new IncrementalIngestRequest(project.Id, source.Id, correctionBatch.Id, [corrected], 0.90, 0.75));
            Assert.Equal(1, result.RecordsCorrected);

            // The superseded record's Lucene doc must be gone.
            Assert.DoesNotContain(index.Retrieve(queryOldEmail, [], profile), c => c.Id == original.Id);

            // The correcting record IS indexed and retrievable under its new value.
            var queryNewEmail = engine.PrepareForStorage(
                Record(project.Id, source.Id, correctionBatch.Id, "probe2",
                    new() { ["source"] = "CRM", ["email"] = "alice@new.example.com" }, now.AddMinutes(1)),
                profile);
            Assert.Contains(index.Retrieve(queryNewEmail, [], profile), c => c.Id == corrected.Id);
        }
        finally
        {
            if (Directory.Exists(indexDir))
                Directory.Delete(indexDir, recursive: true);
        }
    }

    /// <summary>
    /// Regression for #67, reintroduced on Postgres by this milestone until Finding 1's fix: a
    /// 2-member cluster {A,B} with a golden record, where ONE batch corrects BOTH members.
    /// Iteration 1 (correcting A) reduces the cluster to survivor {B} and, because a golden record
    /// already exists, recomputes+queues a new golden record row (still keyed to this same cluster
    /// id) alongside it. Iteration 2 (correcting B) sees the cluster's pending-reduced membership
    /// as already empty, tombstones the cluster, and queues it for golden-record clearing.
    /// ApplyAsync used to run that clear BEFORE the golden-record upsert, so it could only remove
    /// an already-persisted row — never one THIS SAME call just queued for the very cluster it's
    /// also clearing — leaving an orphaned golden_records row pointing at a tombstoned cluster.
    /// ListGoldenRecordsAsync filters by active cluster and would mask this, so this test queries
    /// golden_records directly instead.
    /// </summary>
    [SkippableFact]
    public async Task CorrectingBothMembersOfTwoMemberClusterInOneBatch_LeavesNoOrphanedGoldenRecord()
    {
        Skip.IfNot(shared.IsAvailable, "Docker not available — skipping Testcontainers test");
        var store = _store!;
        var now = DateTimeOffset.UtcNow;

        var project = await store.CreateProjectAsync("Customer MDM", "person", null, now);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now);
        var seedBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 2, now);

        var memberA = Record(project.Id, source.Id, seedBatch.Id, "crm-a", new() { ["name"] = "Alice" }, now);
        var memberB = Record(project.Id, source.Id, seedBatch.Id, "crm-b", new() { ["name"] = "Alice" }, now);
        var clusterId = Guid.NewGuid();
        var goldenId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        await store.SaveCompletedBatchAsync(new CompletedBatchMetadata(
            [memberA, memberB], [],
            [new Cluster { Id = clusterId, ProjectId = project.Id, MemberEntityRecordIds = [memberA.Id, memberB.Id], CreatedAt = now }],
            [new GoldenRecord { Id = goldenId, ProjectId = project.Id, ClusterId = clusterId, CurrentVersionId = versionId, Fields = new Dictionary<string, string> { ["name"] = "Alice" }, UpdatedAt = now }],
            [new GoldenRecordVersion { Id = versionId, GoldenRecordId = goldenId, ProjectId = project.Id, ClusterId = clusterId, IngestBatchId = seedBatch.Id, VersionNumber = 1, Fields = new Dictionary<string, string> { ["name"] = "Alice" }, CreatedAt = now }]));

        var correctionBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 2, now.AddMinutes(1));
        var correctedA = Record(project.Id, source.Id, correctionBatch.Id, "crm-a", new() { ["name"] = "Alice-corrected" }, now.AddMinutes(1));
        var correctedB = Record(project.Id, source.Id, correctionBatch.Id, "crm-b", new() { ["name"] = "Alice-corrected-too" }, now.AddMinutes(1));

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, correctionBatch.Id, [correctedA, correctedB], 0.90, 0.75));
        Assert.Equal(2, result.RecordsCorrected);

        await using var conn = new Npgsql.NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        var clusterStatus = await conn.ExecuteScalarAsync<string>(
            "SELECT status FROM clusters WHERE id = @Id", new { Id = clusterId });
        Assert.Equal("merged", clusterStatus);

        var orphanCount = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM golden_records WHERE cluster_id = @Id", new { Id = clusterId });
        Assert.Equal(0, orphanCount);
    }
}
