using Linkuity.Core.Models;
using Linkuity.Infrastructure.Lucene;
using Linkuity.TestSupport;
using Testcontainers.PostgreSql;

namespace Linkuity.Infrastructure.Postgres.Tests;

/// <summary>
/// Gated on Docker. Proves the bounded, transactional incremental-ingest path on Postgres
/// behaves identically to the File/Local backend for the three core scenarios:
///   1. Shared-email incoming auto-matches an existing record and joins its cluster.
///   2. Two net-new in-batch duplicates co-cluster within one batch.
///   3. Bridge-merge: incoming auto-joins one cluster + auto-matches a second → one merge event.
/// Each test spins its own postgres:16-alpine container + a fresh Lucene index (temp dir),
/// so the store always carries an index and the index Count tracks COUNT(*) entity_records.
/// </summary>
public sealed class PostgresIncrementalIngestTests
{
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

    // ── Test 1 ───────────────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task SharedEmail_Incoming_AutoMatches_AndJoinsExistingCluster()
    {
        Skip.IfNot(DockerProbe.IsAvailable(), "Docker not available — skipping Testcontainers test");

        await using var h = await Harness.CreateAsync();
        var store = h.Store;
        var now = DateTimeOffset.UtcNow;

        var project = await store.CreateProjectAsync("MDM", "person", null, now);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now);
        var seedBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now);

        var existing = Record(project.Id, source.Id, seedBatch.Id, "existing-1",
            new() { ["source"] = "CRM", ["email"] = "alice@example.com", ["name"] = "Alice" }, now);
        var existingClusterId = Guid.NewGuid();
        await store.SaveCompletedBatchAsync(new CompletedBatchMetadata(
            [existing], [],
            [new Cluster { Id = existingClusterId, ProjectId = project.Id, MemberEntityRecordIds = [existing.Id], CreatedAt = now }],
            [], []));

        var incBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1));
        var incoming = Record(project.Id, source.Id, incBatch.Id, "in-1",
            new() { ["source"] = "CRM", ["email"] = "alice@example.com", ["name"] = "Alice Verified" }, now.AddMinutes(1));

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, incBatch.Id, [incoming], 0.90, 0.75));

        Assert.Equal(1, result.AutoMatches);

        var cluster = Assert.Single(await store.ListClustersAsync(project.Id));
        Assert.Equal(existingClusterId, cluster.Id);
        Assert.Equal(2, cluster.MemberEntityRecordIds.Count);
        Assert.Contains(incoming.Id, cluster.MemberEntityRecordIds);

        // Auto-join into an existing cluster recomputes the golden → one new version.
        Assert.Equal(1, result.GoldenRecordVersionsCreated);
        Assert.Single(await store.ListGoldenRecordsAsync(project.Id));
        Assert.Single(await store.ListGoldenRecordVersionsAsync(project.Id));
    }

    // ── Test 2 ───────────────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task TwoNetNewInBatchDuplicates_CoCluster_InOneBatch()
    {
        Skip.IfNot(DockerProbe.IsAvailable(), "Docker not available — skipping Testcontainers test");

        await using var h = await Harness.CreateAsync();
        var store = h.Store;
        var now = DateTimeOffset.UtcNow;

        var project = await store.CreateProjectAsync("MDM", "person", null, now);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 2, now);

        var a = Record(project.Id, source.Id, batch.Id, "in-a",
            new() { ["source"] = "CRM", ["email"] = "maria@x.com", ["name"] = "Maria Garcia" }, now);
        var b = Record(project.Id, source.Id, batch.Id, "in-b",
            new() { ["source"] = "CRM", ["email"] = "maria@x.com", ["name"] = "Maria Garcia" }, now);

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, batch.Id, [a, b], 0.90, 0.75));

        Assert.True(result.AutoMatches >= 1);

        var cluster = Assert.Single(await store.ListClustersAsync(project.Id));
        Assert.Equal(2, cluster.MemberEntityRecordIds.Count);
        Assert.Contains(a.Id, cluster.MemberEntityRecordIds);
        Assert.Contains(b.Id, cluster.MemberEntityRecordIds);
    }

    // ── Test 3 ───────────────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task BridgeMerge_AutoJoinsOneCluster_AndAutoMatchesSecond_MergesIntoOneSurvivor()
    {
        Skip.IfNot(DockerProbe.IsAvailable(), "Docker not available — skipping Testcontainers test");

        await using var h = await Harness.CreateAsync();
        var store = h.Store;
        var now = DateTimeOffset.UtcNow;

        var project = await store.CreateProjectAsync("MDM", "person", null, now);
        var source = await store.CreateSourceAsync(project.Id, "CRM", now);
        var seedBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 2, now);

        // C1 = email-bearing; C2 = phone-bearing; distinct → two separate clusters at seed time.
        var r1 = Record(project.Id, source.Id, seedBatch.Id, "ex-1",
            new() { ["source"] = "CRM", ["email"] = "jon@acme.com", ["name"] = "Jonathan Smith" }, now);
        var r2 = Record(project.Id, source.Id, seedBatch.Id, "ex-2",
            new() { ["source"] = "CRM", ["phone"] = "555-9876", ["name"] = "J Smith" }, now);
        await store.SaveCompletedBatchAsync(new CompletedBatchMetadata(
            [r1, r2], [],
            [
                new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [r1.Id], CreatedAt = now },
                new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [r2.Id], CreatedAt = now.AddSeconds(1) }
            ], [], []));

        var incBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1));
        // X shares email with C1 and phone with C2 → auto into both (identifier flooring) → bridge merge.
        var x = Record(project.Id, source.Id, incBatch.Id, "in-x",
            new() { ["source"] = "CRM", ["email"] = "jon@acme.com", ["phone"] = "555-9876", ["name"] = "Jonathan Smith" }, now.AddMinutes(1));

        await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, incBatch.Id, [x], 0.90, 0.75));

        var active = Assert.Single(await store.ListClustersAsync(project.Id)); // merged into one survivor
        Assert.Equal(3, active.MemberEntityRecordIds.Count);                   // r1, r2, x
        Assert.Contains(x.Id, active.MemberEntityRecordIds);

        var merge = Assert.Single(await store.ListClusterMergeEventsAsync(project.Id));
        Assert.Equal(active.Id, merge.SurvivorClusterId);
        Assert.Contains(x.Id, merge.TriggerRecordIds);
        Assert.Contains(r2.Id, merge.AbsorbedMemberEntityRecordIds); // loser's member retained for lineage

        // Loser cluster is tombstoned (status='merged') and points at the survivor.
        Assert.NotEqual(active.Id, merge.AbsorbedClusterId);

        Assert.Single(await store.ListGoldenRecordsAsync(project.Id)); // one active golden on the survivor
    }

    // ── Harness ──────────────────────────────────────────────────────────────────
    private sealed class Harness : IAsyncDisposable
    {
        public required PostgreSqlContainer Container { get; init; }
        public required LuceneCandidateRetrieval Index { get; init; }
        public required PostgresMetadataStore Store { get; init; }
        public required string IndexDir { get; init; }

        public static async Task<Harness> CreateAsync()
        {
            var pg = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .Build();
            await pg.StartAsync();
            DbUpMigrator.EnsureSchema(pg.GetConnectionString());

            var indexDir = Path.Combine(Path.GetTempPath(), "linkuity-pg-inc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(indexDir);
            var index = new LuceneCandidateRetrieval(
                new LuceneCandidateRetrievalOptions { IndexDirectory = indexDir });

            var store = new PostgresMetadataStore(
                new PostgresMetadataStoreOptions { ConnectionString = pg.GetConnectionString() },
                engine: null,
                profileProvider: null,
                indexedRetrieval: index);

            return new Harness { Container = pg, Index = index, Store = store, IndexDir = indexDir };
        }

        public async ValueTask DisposeAsync()
        {
            Index.Dispose();
            await Container.DisposeAsync();
            try
            {
                if (Directory.Exists(IndexDir))
                    Directory.Delete(IndexDir, recursive: true);
            }
            catch
            {
                // Best-effort temp-dir cleanup.
            }
        }
    }
}
