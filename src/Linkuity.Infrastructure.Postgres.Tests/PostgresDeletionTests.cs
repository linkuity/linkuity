using Linkuity.Core.Models;
using Linkuity.Infrastructure.Lucene;
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
    public async Task DeleteRecordsAsync_OnIndexedStore_ThrowsNotSupported()
    {
        Skip.IfNot(shared.IsAvailable, "Docker not available — skipping Testcontainers test");

        var indexDir = Path.Combine(Path.GetTempPath(), "linkuity-pg-deletion-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(indexDir);
        try
        {
            using var index = new LuceneCandidateRetrieval(new LuceneCandidateRetrievalOptions { IndexDirectory = indexDir });
            var connectionString = await shared.CreateDatabaseAsync(nameof(DeleteRecordsAsync_OnIndexedStore_ThrowsNotSupported));
            DbUpMigrator.EnsureSchema(connectionString);
            var store = new PostgresMetadataStore(
                new PostgresMetadataStoreOptions { ConnectionString = connectionString },
                engine: null, profileProvider: null, indexedRetrieval: index);
            var now = DateTimeOffset.UtcNow;

            var project = await store.CreateProjectAsync("Customer MDM", "person", null, now);
            var source = await store.CreateSourceAsync(project.Id, "CRM", now);
            var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now);
            var original = Record(project.Id, source.Id, batch.Id, "crm-001",
                new() { ["source"] = "CRM", ["email"] = "alice@example.com" }, now);
            await store.SaveIncrementalIngestAsync(
                new IncrementalIngestRequest(project.Id, source.Id, batch.Id, [original], 0.90, 0.75));

            var deletionBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 0, now.AddMinutes(1));
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                store.DeleteRecordsAsync(project.Id, source.Id, deletionBatch.Id, ["crm-001"]));
        }
        finally
        {
            if (Directory.Exists(indexDir))
                Directory.Delete(indexDir, recursive: true);
        }
    }
}
