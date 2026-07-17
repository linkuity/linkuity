using Linkuity.Core.Models;
using Linkuity.TestSupport;
using Testcontainers.PostgreSql;

namespace Linkuity.Infrastructure.Postgres.Tests;

/// <summary>
/// Gated on Docker availability. Verifies SaveCompletedBatchAsync:
/// 1. Recomputes golden fields from the project merge policy (not the imported values).
/// 2. Rolls back completely on validation failure (no orphaned rows).
/// </summary>
public sealed class PostgresCompletedBatchTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private PostgresMetadataStore? _store;

    public async Task InitializeAsync()
    {
        if (!DockerProbe.IsAvailable())
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .Build();

        await _pg.StartAsync();
        DbUpMigrator.EnsureSchema(_pg.GetConnectionString());

        _store = new PostgresMetadataStore(
            new PostgresMetadataStoreOptions { ConnectionString = _pg.GetConnectionString() },
            engine: null,
            profileProvider: null,
            indexedRetrieval: null);
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
            await _pg.DisposeAsync();
    }

    // ── Test 1 ─────────────────────────────────────────────────────────────────
    // A completed batch for a project WITH a MergeConfiguration must recompute
    // the golden record from the merge policy (CRM priority over Marketing).
    // The imported golden had the Marketing email — the store must override it.
    [SkippableFact]
    public async Task SaveCompletedBatch_WithMergePolicy_GoldenReflectsPrioritySource()
    {
        Skip.IfNot(DockerProbe.IsAvailable(), "Docker not available — skipping Testcontainers test");

        var store = _store!;
        var now = DateTimeOffset.UtcNow;

        var project = await store.CreateProjectAsync(
            "MergeTest", "person",
            new MergeConfiguration
            {
                MergeFields =
                [
                    new MergeField { FieldName = "email", SourcePriority = ["CRM", "Marketing"] }
                ]
            },
            now);

        var source = await store.CreateSourceAsync(project.Id, "CRM", now);
        var batch  = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 2, now);

        var crm = new EntityRecord
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, SourceId = source.Id,
            IngestBatchId = batch.Id, SourceRecordId = "crm-001",
            Fields = new Dictionary<string, string>
            {
                ["source"] = "CRM",
                ["email"]  = "crm@example.com",
                ["name"]   = "Alice CRM"
            },
            BlockingKeys = ["email:alice"],
            CreatedAt = now
        };
        var marketing = new EntityRecord
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, SourceId = source.Id,
            IngestBatchId = batch.Id, SourceRecordId = "mkt-001",
            Fields = new Dictionary<string, string>
            {
                ["source"] = "Marketing",
                ["email"]  = "marketing@example.com",
                ["name"]   = "Alice Marketing"
            },
            BlockingKeys = ["email:alice"],
            CreatedAt = now
        };
        var clusterId = Guid.NewGuid();

        // The imported golden deliberately carries the Marketing email — the store
        // must ignore it and recompute from merge policy (CRM wins).
        await store.SaveCompletedBatchAsync(new CompletedBatchMetadata(
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

        // Verify: golden fields must reflect CRM priority, not the imported Marketing value.
        var golden = Assert.Single(await store.ListGoldenRecordsAsync(project.Id));
        Assert.Equal("crm@example.com", golden.Fields["email"]);

        var version = Assert.Single(await store.ListGoldenRecordVersionsAsync(project.Id));
        Assert.Equal("crm@example.com", version.Fields["email"]);

        // Entity records and cluster must be persisted too.
        Assert.Equal(2, (await store.ListEntityRecordsAsync(project.Id)).Count);
        var cluster = Assert.Single(await store.ListClustersAsync(project.Id));
        Assert.Equal(2, cluster.MemberEntityRecordIds.Count);
    }

    // ── Test 2 ─────────────────────────────────────────────────────────────────
    // A validation failure (duplicate source-record-id) must throw and leave the
    // DB completely empty — proving the transaction was rolled back.
    [SkippableFact]
    public async Task SaveCompletedBatch_DuplicateSourceRecordId_ThrowsAndRollsBack()
    {
        Skip.IfNot(DockerProbe.IsAvailable(), "Docker not available — skipping Testcontainers test");

        var store = _store!;
        var now = DateTimeOffset.UtcNow;

        var project = await store.CreateProjectAsync("RollbackTest", "person", null, now);
        var source  = await store.CreateSourceAsync(project.Id, "CRM", now);
        var batch   = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 2, now);

        var r1 = new EntityRecord
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, SourceId = source.Id,
            IngestBatchId = batch.Id, SourceRecordId = "dup-id",
            Fields = new Dictionary<string, string> { ["email"] = "a@example.com" },
            BlockingKeys = [],
            CreatedAt = now
        };
        var r2 = new EntityRecord
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, SourceId = source.Id,
            IngestBatchId = batch.Id, SourceRecordId = "dup-id", // same source-record-id → invalid
            Fields = new Dictionary<string, string> { ["email"] = "b@example.com" },
            BlockingKeys = [],
            CreatedAt = now
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveCompletedBatchAsync(new CompletedBatchMetadata(
                [r1, r2], [], [], [], [])));

        Assert.Contains("Duplicate source record id", ex.Message);

        // Rollback must have left no entity records.
        Assert.Empty(await store.ListEntityRecordsAsync(project.Id));
    }

    // ── Test 3 ─────────────────────────────────────────────────────────────────
    // When a completed batch collides with an existing entity record under different
    // casing, the "already exists" message must report the INCOMING source_record_id
    // (parity with FileMetadataStore), not the DB-stored value.
    [SkippableFact]
    public async Task SaveCompletedBatch_ExistingSourceRecordDifferentCasing_ReportsIncomingId()
    {
        Skip.IfNot(DockerProbe.IsAvailable(), "Docker not available — skipping Testcontainers test");

        var store = _store!;
        var now = DateTimeOffset.UtcNow;

        var project = await store.CreateProjectAsync("CasingTest", "person", null, now);
        var source  = await store.CreateSourceAsync(project.Id, "CRM", now);

        // Seed an existing entity record with an UPPER-case source_record_id.
        var firstBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now);
        var seeded = new EntityRecord
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, SourceId = source.Id,
            IngestBatchId = firstBatch.Id, SourceRecordId = "CRM-001",
            Fields = new Dictionary<string, string> { ["email"] = "a@example.com" },
            BlockingKeys = ["email:a@example.com"],
            CreatedAt = now
        };
        await store.SaveCompletedBatchAsync(new CompletedBatchMetadata(
            [seeded], [],
            [new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [seeded.Id], CreatedAt = now }],
            [], []));

        // Attempt a completed batch containing the SAME id in lower case.
        var secondBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1));
        var incoming = new EntityRecord
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, SourceId = source.Id,
            IngestBatchId = secondBatch.Id, SourceRecordId = "crm-001",
            Fields = new Dictionary<string, string> { ["email"] = "b@example.com" },
            BlockingKeys = ["email:b@example.com"],
            CreatedAt = now.AddMinutes(1)
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveCompletedBatchAsync(new CompletedBatchMetadata(
                [incoming], [],
                [new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [incoming.Id], CreatedAt = now.AddMinutes(1) }],
                [], [])));

        // Must report the incoming value ("crm-001"), not the DB-stored "CRM-001".
        Assert.Contains($"Entity record already exists for project {project.Id}: crm-001", ex.Message);
        Assert.DoesNotContain("CRM-001", ex.Message);
    }
}
