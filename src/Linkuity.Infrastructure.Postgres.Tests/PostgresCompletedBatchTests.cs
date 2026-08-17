using Dapper;
using Linkuity.Core.Models;
using Linkuity.Infrastructure.Lucene;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.TestSupport;
using Npgsql;

namespace Linkuity.Infrastructure.Postgres.Tests;

/// <summary>
/// Gated on Docker availability. Verifies SaveCompletedBatchAsync:
/// 1. Recomputes golden fields from the project merge policy (not the imported values).
/// 2. Rolls back completely on validation failure (no orphaned rows).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresCompletedBatchTests(SharedPostgresContainer shared) : IAsyncLifetime
{
    private PostgresMetadataStore? _store;
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        if (!shared.IsAvailable)
            return;

        _connectionString = await shared.CreateDatabaseAsync(nameof(PostgresCompletedBatchTests));
        DbUpMigrator.EnsureSchema(_connectionString);

        _store = new PostgresMetadataStore(
            new PostgresMetadataStoreOptions { ConnectionString = _connectionString },
            engine: null,
            profileProvider: null,
            indexedRetrieval: null);
    }

    // The container outlives this class; the database is left in place for post-mortem.
    public Task DisposeAsync() => Task.CompletedTask;

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

    // ── Test 4 ─────────────────────────────────────────────────────────────────
    // The break this catches: moving IndexRecords(...) back ABOVE `await tx.CommitAsync(ct)` in
    // SaveCompletedBatchAsync (the ordering it shipped with before #85). That ordering lets Lucene
    // durably serve documents for rows that never committed if the SQL commit then fails, with no
    // self-healing path back — the same defect class #66 fixed on the correction/deletion paths.
    // Asserting index contents after the call returns cannot catch it: both orderings end with
    // identical DB and index state on the success path. Only probing over a SEPARATE connection
    // from INSIDE the index mutation distinguishes them — under READ COMMITTED that connection
    // cannot see the store's still-uncommitted INSERT, so it reports false iff the index was
    // mutated too early. This is the PostgreSQL half of the file store's
    // SaveCompletedBatchAsync_OnIndexedStore_CommitsDurableWriteBeforeMutatingIndex.
    [SkippableFact]
    public async Task SaveCompletedBatch_OnIndexedStore_CommitsSqlTransactionBeforeMutatingIndex()
    {
        Skip.IfNot(DockerProbe.IsAvailable(), "Docker not available — skipping Testcontainers test");

        // Its own database, not the class's shared one: SaveCompletedBatchAsync's drift check
        // counts entity_records GLOBALLY, not per project, so rows left by the other tests in this
        // class would make a fresh index look drifted and fire a rebuild — an index mutation that
        // legitimately runs BEFORE the durable write and would fail the ordering assertion below
        // for a reason that has nothing to do with the invariant under test. An empty database
        // starts the store on the normal path (counts agree, no rebuild).
        var connectionString = await shared.CreateDatabaseAsync(nameof(SaveCompletedBatch_OnIndexedStore_CommitsSqlTransactionBeforeMutatingIndex));
        DbUpMigrator.EnsureSchema(connectionString);
        var indexDir = Path.Combine(Path.GetTempPath(), $"linkuity-pg-completed-batch-ordering-{Guid.NewGuid():N}");
        Directory.CreateDirectory(indexDir);
        try
        {
            var engine = MatchingDefaults.CreateEngine();
            var profile = DefaultMatchingProfileProvider.CreatePersonProfile();
            var provider = new DefaultMatchingProfileProvider([profile]);
            using var lucene = new LuceneCandidateRetrieval(new LuceneCandidateRetrievalOptions { IndexDirectory = indexDir });

            // The id is fixed up front so the probe can close over it before the row exists.
            var recordId = Guid.NewGuid();
            var spy = new DurableWriteOrderingSpyIndex(lucene, () => RowIsCommitted(connectionString, recordId));
            var store = new PostgresMetadataStore(
                new PostgresMetadataStoreOptions { ConnectionString = connectionString },
                engine, provider, spy);

            var now = DateTimeOffset.UtcNow;
            var project = await store.CreateProjectAsync("OrderingTest", "person", null, now);
            var source = await store.CreateSourceAsync(project.Id, "CRM", now);
            var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now);
            var record = new EntityRecord
            {
                Id = recordId,
                ProjectId = project.Id,
                SourceId = source.Id,
                IngestBatchId = batch.Id,
                SourceRecordId = "ordering-001",
                Fields = new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "alice@example.com" },
                BlockingKeys = [],
                CreatedAt = now
            };

            await store.SaveCompletedBatchAsync(new CompletedBatchMetadata(
                [record], [],
                [new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [recordId], CreatedAt = now }],
                [], []));

            // Guards against a vacuous pass: if the store stopped mutating the index at all, the
            // Assert.All below would be trivially satisfied.
            Assert.NotEmpty(spy.Mutations);
            Assert.All(spy.DurableWriteVisibleAtMutation, Assert.True);

            // ...and the row really is retrievable from the index afterwards, so "index nothing" is
            // not a way to satisfy the ordering assertion.
            var queryProbe = engine.PrepareForStorage(
                new EntityRecord
                {
                    Id = Guid.NewGuid(),
                    ProjectId = project.Id,
                    SourceId = source.Id,
                    IngestBatchId = batch.Id,
                    SourceRecordId = "probe",
                    Fields = new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "alice@example.com" },
                    BlockingKeys = [],
                    CreatedAt = now
                },
                profile);
            Assert.Contains(lucene.Retrieve(queryProbe, [], profile), c => c.Id == recordId);
        }
        finally
        {
            if (Directory.Exists(indexDir))
                Directory.Delete(indexDir, recursive: true);
        }
    }

    // ── Test 5 ─────────────────────────────────────────────────────────────────
    // SaveIncrementalIngestAsync (step 6) and DeleteRecordsAsync both call EnsureIndexCurrentAsync
    // before mutating the index; SaveCompletedBatchAsync must too, or a pre-existing drift (e.g. an
    // earlier crash between a commit and its index mutation) is never repaired on the bulk-import
    // path — a batch-only workflow would carry it forever. The break this catches: removing the
    // EnsureIndexCurrentAsync call from SaveCompletedBatchAsync. This is the PostgreSQL half of the
    // file store's SaveCompletedBatchAsync_IndexAlreadyDrifted_SelfHealsBeforeIndexingBatch.
    [SkippableFact]
    public async Task SaveCompletedBatch_IndexAlreadyDrifted_SelfHealsBeforeIndexingBatch()
    {
        Skip.IfNot(DockerProbe.IsAvailable(), "Docker not available — skipping Testcontainers test");

        // Its own database, for the same reason as Test 4: the drift check counts entity_records
        // globally, so rows from the other tests in this class would make the mismatch (and the
        // rebuilt document set) depend on which of them happened to run first.
        var connectionString = await shared.CreateDatabaseAsync(nameof(SaveCompletedBatch_IndexAlreadyDrifted_SelfHealsBeforeIndexingBatch));
        DbUpMigrator.EnsureSchema(connectionString);
        var indexDir = Path.Combine(Path.GetTempPath(), $"linkuity-pg-completed-batch-selfheal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(indexDir);
        try
        {
            var engine = MatchingDefaults.CreateEngine();
            var profile = DefaultMatchingProfileProvider.CreatePersonProfile();
            var provider = new DefaultMatchingProfileProvider([profile]);
            using var index = new LuceneCandidateRetrieval(new LuceneCandidateRetrievalOptions { IndexDirectory = indexDir });
            var store = new PostgresMetadataStore(
                new PostgresMetadataStoreOptions { ConnectionString = connectionString },
                engine, provider, index);

            var now = DateTimeOffset.UtcNow;
            var project = await store.CreateProjectAsync("SelfHealTest", "person", null, now);
            var source = await store.CreateSourceAsync(project.Id, "CRM", now);
            var seedBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now);
            var keep = new EntityRecord
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                SourceId = source.Id,
                IngestBatchId = seedBatch.Id,
                SourceRecordId = "crm-keep",
                Fields = new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "keep@example.com" },
                BlockingKeys = [],
                CreatedAt = now
            };
            await store.SaveIncrementalIngestAsync(
                new IncrementalIngestRequest(project.Id, source.Id, seedBatch.Id, [keep], 0.90, 0.75));

            // Simulate drift external to this call — a crash between a commit and its index
            // mutation — by dropping "keep"'s doc without touching the database.
            index.Remove(keep.Id);
            index.Commit();

            var importBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1));
            var imported = new EntityRecord
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                SourceId = source.Id,
                IngestBatchId = importBatch.Id,
                SourceRecordId = "crm-imported",
                Fields = new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "imported@example.com" },
                BlockingKeys = [],
                CreatedAt = now.AddMinutes(1)
            };
            await store.SaveCompletedBatchAsync(new CompletedBatchMetadata(
                [imported], [],
                [new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [imported.Id], CreatedAt = now.AddMinutes(1) }],
                [], []));

            // "keep" must be back — the drift check should have rebuilt from live rows before the
            // batch's own records were indexed.
            Assert.Contains(index.Retrieve(Probe(project, source, seedBatch, "keep@example.com", now), [], profile),
                c => c.Id == keep.Id);

            // ...and the imported record is indexed too, so a rebuild that clobbered the batch
            // would not pass either.
            Assert.Contains(index.Retrieve(Probe(project, source, importBatch, "imported@example.com", now), [], profile),
                c => c.Id == imported.Id);

            EntityRecord Probe(Project p, Source s, IngestBatch b, string email, DateTimeOffset at)
                => engine.PrepareForStorage(
                    new EntityRecord
                    {
                        Id = Guid.NewGuid(),
                        ProjectId = p.Id,
                        SourceId = s.Id,
                        IngestBatchId = b.Id,
                        SourceRecordId = "probe",
                        Fields = new Dictionary<string, string> { ["source"] = "CRM", ["email"] = email },
                        BlockingKeys = [],
                        CreatedAt = at
                    },
                    profile);
        }
        finally
        {
            if (Directory.Exists(indexDir))
                Directory.Delete(indexDir, recursive: true);
        }
    }

    // Synchronous by necessity — it runs inside the index mutation callback, which the
    // IIndexedCandidateRetrievalStrategy contract defines as synchronous. A plain SELECT never
    // blocks on the store's uncommitted INSERT under MVCC, so this cannot deadlock against the
    // transaction it is probing; it simply does not see uncommitted rows.
    private static bool RowIsCommitted(string connectionString, Guid recordId)
    {
        using var probe = new NpgsqlConnection(connectionString);
        probe.Open();
        return probe.ExecuteScalar<bool>(
            "SELECT EXISTS(SELECT 1 FROM entity_records WHERE id = @Id)", new { Id = recordId });
    }
}
