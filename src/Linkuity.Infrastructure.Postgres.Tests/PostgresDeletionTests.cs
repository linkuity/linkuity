using Dapper;
using Linkuity.Core.Models;
using Linkuity.Infrastructure.Lucene;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.TestSupport;

namespace Linkuity.Infrastructure.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresDeletionTests(SharedPostgresContainer shared) : IAsyncLifetime
{
    private string? _connectionString;
    private PostgresMetadataStore? _store;

    public async Task InitializeAsync()
    {
        if (!shared.IsAvailable)
            return; // tests will self-skip

        _connectionString = await shared.CreateDatabaseAsync(nameof(PostgresDeletionTests));
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
    public async Task DeleteRecordsAsync_ExistingRecord_MarksDeletedAndDetachesFromCluster()
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

        var deletionBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 0, now.AddMinutes(1));
        var result = await store.DeleteRecordsAsync(project.Id, source.Id, deletionBatch.Id, ["crm-001"]);

        Assert.Equal(1, result.RecordsDeleted);
        Assert.Empty(await store.ListEntityRecordsAsync(project.Id));

        var events = await store.ListRecordDeletedEventsAsync(project.Id);
        var evt = Assert.Single(events);
        Assert.Equal("alice@example.com", evt.PreviousFields["email"]);
    }

    [SkippableFact]
    public async Task DeleteRecordsAsync_DeletingSameSourceRecordIdTwice_SecondCallThrows()
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

        var deletionBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 0, now.AddMinutes(1));
        await store.DeleteRecordsAsync(project.Id, source.Id, deletionBatch.Id, ["crm-001"]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DeleteRecordsAsync(project.Id, source.Id, deletionBatch.Id, ["crm-001"]));
        Assert.Contains("crm-001", ex.Message);
    }

    /// <summary>
    /// The Postgres-specific regression this milestone exists to prevent, for deletion — mirrors
    /// PostgresCorrectionTests.Correction_ClearsClusterIdOnDepartingRecord_SurvivorKeepsCorrectMembership.
    /// </summary>
    [SkippableFact]
    public async Task DeleteRecordsAsync_ClearsClusterIdOnDeletedRecord_SurvivorKeepsCorrectMembership()
    {
        Skip.IfNot(shared.IsAvailable, "Docker not available — skipping Testcontainers test");
        var store = _store!;
        var now = DateTimeOffset.UtcNow;

        var project = await store.CreateProjectAsync("Customer MDM", "person", null, now);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now);
        var seedBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 2, now);

        var toDelete = Record(project.Id, source.Id, seedBatch.Id, "crm-001",
            new() { ["source"] = "CRM", ["email"] = "shared@example.com", ["name"] = "Alice" }, now);
        var survivor = Record(project.Id, source.Id, seedBatch.Id, "crm-002",
            new() { ["source"] = "CRM", ["email"] = "shared@example.com", ["name"] = "Bob" }, now);
        var clusterId = Guid.NewGuid();
        await store.SaveCompletedBatchAsync(new CompletedBatchMetadata(
            [toDelete, survivor], [],
            [new Cluster { Id = clusterId, ProjectId = project.Id, MemberEntityRecordIds = [toDelete.Id, survivor.Id], CreatedAt = now }],
            [], []));

        var deletionBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 0, now.AddMinutes(1));
        var result = await store.DeleteRecordsAsync(project.Id, source.Id, deletionBatch.Id, ["crm-001"]);
        Assert.Equal(1, result.RecordsDeleted);

        var activeCluster = Assert.Single(await store.ListClustersAsync(project.Id));
        Assert.Equal(clusterId, activeCluster.Id);
        Assert.Equal([survivor.Id], activeCluster.MemberEntityRecordIds);
    }

    [SkippableFact]
    public async Task DeleteRecordsAsync_ClusterHadOnlyThisMember_ClusterTombstoned()
    {
        Skip.IfNot(shared.IsAvailable, "Docker not available — skipping Testcontainers test");
        var store = _store!;
        var now = DateTimeOffset.UtcNow;

        var project = await store.CreateProjectAsync("Customer MDM", "person", null, now);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now);
        var seedBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now);

        var only = Record(project.Id, source.Id, seedBatch.Id, "crm-001",
            new() { ["source"] = "CRM", ["email"] = "alice@example.com" }, now);
        var clusterId = Guid.NewGuid();
        await store.SaveCompletedBatchAsync(new CompletedBatchMetadata(
            [only], [],
            [new Cluster { Id = clusterId, ProjectId = project.Id, MemberEntityRecordIds = [only.Id], CreatedAt = now }],
            [], []));

        var deletionBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 0, now.AddMinutes(1));
        await store.DeleteRecordsAsync(project.Id, source.Id, deletionBatch.Id, ["crm-001"]);

        var clusters = await store.ListClustersAsync(project.Id);
        Assert.DoesNotContain(clusters, c => c.Id == clusterId);
    }

    [SkippableFact]
    public async Task DeleteRecordsAsync_UnknownSourceRecordId_Throws()
    {
        Skip.IfNot(shared.IsAvailable, "Docker not available — skipping Testcontainers test");
        var store = _store!;
        var now = DateTimeOffset.UtcNow;

        var project = await store.CreateProjectAsync("Customer MDM", "person", null, now);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 0, now);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DeleteRecordsAsync(project.Id, source.Id, batch.Id, ["nonexistent"]));
        Assert.Contains("nonexistent", ex.Message);
    }

    [SkippableFact]
    public async Task DeleteRecordsAsync_UnknownProjectSourceOrBatch_Throws()
    {
        Skip.IfNot(shared.IsAvailable, "Docker not available — skipping Testcontainers test");
        var store = _store!;
        var now = DateTimeOffset.UtcNow;

        var project = await store.CreateProjectAsync("Customer MDM", "person", null, now);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 0, now);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DeleteRecordsAsync(Guid.NewGuid(), source.Id, batch.Id, ["x"]));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DeleteRecordsAsync(project.Id, Guid.NewGuid(), batch.Id, ["x"]));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DeleteRecordsAsync(project.Id, source.Id, Guid.NewGuid(), ["x"]));
    }

    [SkippableFact]
    public async Task DeleteRecordsAsync_DuplicateSourceRecordIdInOneCall_Throws()
    {
        Skip.IfNot(shared.IsAvailable, "Docker not available — skipping Testcontainers test");
        var store = _store!;
        var now = DateTimeOffset.UtcNow;

        var project = await store.CreateProjectAsync("Customer MDM", "person", null, now);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 0, now);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DeleteRecordsAsync(project.Id, source.Id, batch.Id, ["crm-001", "crm-001"]));
        Assert.Contains("Duplicate", ex.Message);
    }

    [SkippableFact]
    public async Task DeleteRecordsAsync_OnIndexedStore_RemovesFromIndex()
    {
        Skip.IfNot(shared.IsAvailable, "Docker not available — skipping Testcontainers test");

        var indexDir = Path.Combine(Path.GetTempPath(), "linkuity-pg-deletion-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(indexDir);
        try
        {
            using var index = new LuceneCandidateRetrieval(new LuceneCandidateRetrievalOptions { IndexDirectory = indexDir });
            var connectionString = await shared.CreateDatabaseAsync(nameof(DeleteRecordsAsync_OnIndexedStore_RemovesFromIndex));
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
                new() { ["source"] = "CRM", ["email"] = "alice@example.com" }, now);
            await store.SaveIncrementalIngestAsync(
                new IncrementalIngestRequest(project.Id, source.Id, batch.Id, [original], 0.90, 0.75));

            // Blocking keys must be generated the same way the store generates them for real
            // incoming records (engine.PrepareForStorage), or the query never builds a search term.
            var queryProbe = engine.PrepareForStorage(
                Record(project.Id, source.Id, batch.Id, "probe",
                    new() { ["source"] = "CRM", ["email"] = "alice@example.com" }, now.AddMinutes(1)),
                profile);
            // Prove the probe is capable of finding the record before deletion, so the later
            // Assert.Empty means "removed", not "this query never matches anything."
            Assert.Contains(index.Retrieve(queryProbe, [], profile), c => c.Id == original.Id);

            var deletionBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 0, now.AddMinutes(1));
            var result = await store.DeleteRecordsAsync(project.Id, source.Id, deletionBatch.Id, ["crm-001"]);
            Assert.Equal(1, result.RecordsDeleted);

            // The deleted record's Lucene doc must be gone.
            Assert.Empty(index.Retrieve(queryProbe, [], profile));
        }
        finally
        {
            if (Directory.Exists(indexDir))
                Directory.Delete(indexDir, recursive: true);
        }
    }

    [SkippableFact]
    public async Task DeleteRecordsAsync_IndexAlreadyDrifted_SelfHealsBeforeRemoving()
    {
        Skip.IfNot(shared.IsAvailable, "Docker not available — skipping Testcontainers test");

        // SaveIncrementalIngestAsync calls EnsureIndexCurrentAsync before mutating the index;
        // DeleteRecordsAsync must too, or a pre-existing drift (e.g. from a prior crash) is never
        // repaired on the deletion path — it just silently persists.
        var indexDir = Path.Combine(Path.GetTempPath(), "linkuity-pg-deletion-selfheal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(indexDir);
        try
        {
            using var index = new LuceneCandidateRetrieval(new LuceneCandidateRetrievalOptions { IndexDirectory = indexDir });
            var connectionString = await shared.CreateDatabaseAsync(nameof(DeleteRecordsAsync_IndexAlreadyDrifted_SelfHealsBeforeRemoving));
            DbUpMigrator.EnsureSchema(connectionString);
            var store = new PostgresMetadataStore(
                new PostgresMetadataStoreOptions { ConnectionString = connectionString },
                engine: null, profileProvider: null, indexedRetrieval: index);
            var engine = MatchingDefaults.CreateEngine();
            var profile = DefaultMatchingProfileProvider.CreatePersonProfile();
            var now = DateTimeOffset.UtcNow;

            var project = await store.CreateProjectAsync("Customer MDM", "person", null, now);
            var source = await store.CreateSourceAsync(project.Id, "CRM", now);
            var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 2, now);
            var keep = Record(project.Id, source.Id, batch.Id, "crm-keep",
                new() { ["source"] = "CRM", ["email"] = "keep@example.com" }, now);
            var toDelete = Record(project.Id, source.Id, batch.Id, "crm-delete",
                new() { ["source"] = "CRM", ["email"] = "delete@example.com" }, now);
            await store.SaveIncrementalIngestAsync(
                new IncrementalIngestRequest(project.Id, source.Id, batch.Id, [keep, toDelete], 0.90, 0.75));

            // Simulate drift external to this deletion call — e.g. an earlier crash between a
            // Lucene commit and its durable write — by removing "keep"'s doc directly, without
            // touching the durable store. The db still considers it live; the index no longer does.
            index.Remove(keep.Id);
            index.Commit();

            var deletionBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 0, now.AddMinutes(1));
            await store.DeleteRecordsAsync(project.Id, source.Id, deletionBatch.Id, ["crm-delete"]);

            // "keep" must be back in the index — EnsureIndexCurrentAsync should have detected the
            // drift and rebuilt from live durable records before the deletion-specific removal ran.
            var queryKeep = engine.PrepareForStorage(
                Record(project.Id, source.Id, batch.Id, "probe",
                    new() { ["source"] = "CRM", ["email"] = "keep@example.com" }, now.AddMinutes(1)),
                profile);
            Assert.Contains(index.Retrieve(queryKeep, [], profile), c => c.Id == keep.Id);
        }
        finally
        {
            if (Directory.Exists(indexDir))
                Directory.Delete(indexDir, recursive: true);
        }
    }

    [SkippableFact]
    public async Task DeleteRecordsAsync_AfterDeletionOnIndexedStore_SubsequentIngestDoesNotResurrectDeletedCandidate()
    {
        Skip.IfNot(shared.IsAvailable, "Docker not available — skipping Testcontainers test");

        // EnsureIndexCurrentAsync compares the LIVE Lucene doc count to COUNT(*) FROM
        // entity_records and does a full Rebuild on any mismatch. Deletion keeps the tombstoned
        // row in entity_records (superseded_at/deleted_at is never a hard delete) while removing
        // its live Lucene doc, so that comparison must count only LIVE records on both sides —
        // otherwise the very next SaveIncrementalIngestAsync call sees a mismatch and rebuilds
        // the index from every row including tombstoned ones, undoing the deletion.
        var indexDir = Path.Combine(Path.GetTempPath(), "linkuity-pg-deletion-drift-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(indexDir);
        try
        {
            using var index = new LuceneCandidateRetrieval(new LuceneCandidateRetrievalOptions { IndexDirectory = indexDir });
            var connectionString = await shared.CreateDatabaseAsync(nameof(DeleteRecordsAsync_AfterDeletionOnIndexedStore_SubsequentIngestDoesNotResurrectDeletedCandidate));
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
                new() { ["source"] = "CRM", ["email"] = "deleted@example.com" }, now);
            await store.SaveIncrementalIngestAsync(
                new IncrementalIngestRequest(project.Id, source.Id, batch.Id, [original], 0.90, 0.75));

            var queryProbe = engine.PrepareForStorage(
                Record(project.Id, source.Id, batch.Id, "probe",
                    new() { ["source"] = "CRM", ["email"] = "deleted@example.com" }, now.AddMinutes(1)),
                profile);

            var deletionBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 0, now.AddMinutes(1));
            await store.DeleteRecordsAsync(project.Id, source.Id, deletionBatch.Id, ["crm-001"]);
            Assert.Empty(index.Retrieve(queryProbe, [], profile));

            // An unrelated ingest call exercises EnsureIndexCurrentAsync's count check again.
            var unrelatedBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(2));
            var unrelated = Record(project.Id, source.Id, unrelatedBatch.Id, "crm-002",
                new() { ["source"] = "CRM", ["email"] = "unrelated@example.com" }, now.AddMinutes(2));
            await store.SaveIncrementalIngestAsync(
                new IncrementalIngestRequest(project.Id, source.Id, unrelatedBatch.Id, [unrelated], 0.90, 0.75));

            Assert.Empty(index.Retrieve(queryProbe, [], profile));
        }
        finally
        {
            if (Directory.Exists(indexDir))
                Directory.Delete(indexDir, recursive: true);
        }
    }

    /// <summary>
    /// Regression for #67, reintroduced on Postgres by this milestone until Finding 1's fix — the
    /// deletion equivalent of PostgresCorrectionTests.CorrectingBothMembersOfTwoMemberClusterInOneBatch_LeavesNoOrphanedGoldenRecord.
    /// See that test's doc comment for the full mechanism.
    /// </summary>
    [SkippableFact]
    public async Task DeleteRecordsAsync_DeletingBothMembersOfTwoMemberClusterInOneBatch_LeavesNoOrphanedGoldenRecord()
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

        var deletionBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 0, now.AddMinutes(1));
        var result = await store.DeleteRecordsAsync(project.Id, source.Id, deletionBatch.Id, ["crm-a", "crm-b"]);
        Assert.Equal(2, result.RecordsDeleted);

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
