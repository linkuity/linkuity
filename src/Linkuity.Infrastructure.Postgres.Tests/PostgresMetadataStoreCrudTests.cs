using Linkuity.Core.Models;
using Linkuity.TestSupport;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.PostgreSql;

namespace Linkuity.Infrastructure.Postgres.Tests;

/// <summary>
/// Smoke tests for PostgresMetadataStore CRUD and read-back paths.
/// Gated on Docker availability; spins a postgres:16-alpine Testcontainer per class.
/// </summary>
public sealed class PostgresMetadataStoreCrudTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private string? _connectionString;
    private PostgresMetadataStore? _store;

    public async Task InitializeAsync()
    {
        if (!DockerProbe.IsAvailable())
            return; // tests will self-skip

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .Build();

        await _pg.StartAsync();
        _connectionString = _pg.GetConnectionString();

        DbUpMigrator.EnsureSchema(_connectionString);

        _store = new PostgresMetadataStore(
            new PostgresMetadataStoreOptions { ConnectionString = _connectionString },
            engine: null,
            profileProvider: null,
            indexedRetrieval: null);
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
            await _pg.DisposeAsync();
    }

    [SkippableFact]
    public async Task ProjectSourceBatch_RoundTrip_WithMergeConfiguration()
    {
        Skip.IfNot(DockerProbe.IsAvailable(), "Docker not available — skipping Testcontainers test");

        var store = _store!;

        // ── Create project with MergeConfiguration (jsonb) ──────────────────────
        var mergeConfig = new MergeConfiguration
        {
            MergeFields =
            [
                new MergeField { FieldName = "first_name", SourcePriority = ["src_a", "src_b"] },
                new MergeField { FieldName = "last_name",  SourcePriority = ["src_a"] }
            ]
        };
        var createdProject = await store.CreateProjectAsync(
            "SmokeProject", "person", mergeConfig, DateTimeOffset.UtcNow);

        Assert.Equal("SmokeProject", createdProject.Name);
        Assert.NotNull(createdProject.MergeConfiguration);
        Assert.Equal(2, createdProject.MergeConfiguration.MergeFields.Count);

        // Read back via ListProjectsAsync — proves jsonb round-trip through DB
        var projects = await store.ListProjectsAsync();
        var readProject = Assert.Single(projects, p => p.Id == createdProject.Id);
        Assert.NotNull(readProject.MergeConfiguration);
        Assert.Equal(2, readProject.MergeConfiguration.MergeFields.Count);
        Assert.Equal("first_name", readProject.MergeConfiguration.MergeFields[0].FieldName);
        Assert.Equal(new[] { "src_a", "src_b" }, readProject.MergeConfiguration.MergeFields[0].SourcePriority);
        Assert.Equal("last_name", readProject.MergeConfiguration.MergeFields[1].FieldName);

        // GetProjectAsync
        var gotProject = await store.GetProjectAsync(createdProject.Id);
        Assert.NotNull(gotProject);
        Assert.Equal("SmokeProject", gotProject.Name);

        // ── UpdateProjectMergePolicyAsync ────────────────────────────────────────
        var updatedMergeConfig = new MergeConfiguration
        {
            MergeFields = [new MergeField { FieldName = "email", SourcePriority = ["src_a"] }]
        };
        var updatedProject = await store.UpdateProjectMergePolicyAsync(createdProject.Id, updatedMergeConfig);
        Assert.NotNull(updatedProject.MergeConfiguration);
        Assert.Single(updatedProject.MergeConfiguration.MergeFields);
        Assert.Equal("email", updatedProject.MergeConfiguration.MergeFields[0].FieldName);

        // ── Source ───────────────────────────────────────────────────────────────
        var source = await store.CreateSourceAsync(createdProject.Id, "SmokeSource", DateTimeOffset.UtcNow);
        Assert.Equal(createdProject.Id, source.ProjectId);
        Assert.Equal("SmokeSource", source.Name);

        var sources = await store.ListSourcesAsync(createdProject.Id);
        Assert.Contains(sources, s => s.Id == source.Id);

        var gotSource = await store.GetSourceAsync(source.Id);
        Assert.NotNull(gotSource);
        Assert.Equal(source.Id, gotSource.Id);

        // ── IngestBatch ──────────────────────────────────────────────────────────
        var batch = await store.CreateIngestBatchAsync(
            createdProject.Id, source.Id, jobId: null, recordCount: 5, DateTimeOffset.UtcNow);
        Assert.Equal(createdProject.Id, batch.ProjectId);
        Assert.Equal(source.Id, batch.SourceId);
        Assert.Equal(5, batch.RecordCount);

        var batches = await store.ListIngestBatchesAsync(createdProject.Id);
        Assert.Contains(batches, b => b.Id == batch.Id);
    }

    [SkippableFact]
    public async Task EntityRecord_And_Cluster_HydrationRoundTrip()
    {
        Skip.IfNot(DockerProbe.IsAvailable(), "Docker not available — skipping Testcontainers test");

        var store = _store!;
        var cs    = _connectionString!;

        // Set up project / source / batch
        var project = await store.CreateProjectAsync("HydrationProject", "person", null, DateTimeOffset.UtcNow);
        var source  = await store.CreateSourceAsync(project.Id, "HydSrc", DateTimeOffset.UtcNow);
        var batch   = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, DateTimeOffset.UtcNow);

        var clusterId = Guid.NewGuid();
        var recordId  = Guid.NewGuid();

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();

        // Insert cluster
        await using (var clusterCmd = new NpgsqlCommand(
            "INSERT INTO clusters (id, project_id, status, created_at) VALUES (@id, @pid, 'active', @cat)",
            conn))
        {
            clusterCmd.Parameters.AddWithValue("id",  clusterId);
            clusterCmd.Parameters.AddWithValue("pid", project.Id);
            clusterCmd.Parameters.AddWithValue("cat", DateTime.UtcNow);
            await clusterCmd.ExecuteNonQueryAsync();
        }

        // Insert entity_record with jsonb fields + text[] blocking_keys + cluster_id
        await using (var recCmd = new NpgsqlCommand(
            """
            INSERT INTO entity_records
                (id, project_id, source_id, ingest_batch_id, source_record_id,
                 fields, blocking_keys, cluster_id, created_at)
            VALUES
                (@id, @pid, @sid, @bid, @srId,
                 @fields::jsonb, @bkeys, @cid, @cat)
            """,
            conn))
        {
            recCmd.Parameters.AddWithValue("id",   recordId);
            recCmd.Parameters.AddWithValue("pid",  project.Id);
            recCmd.Parameters.AddWithValue("sid",  source.Id);
            recCmd.Parameters.AddWithValue("bid",  batch.Id);
            recCmd.Parameters.AddWithValue("srId", "src-rec-001");
            recCmd.Parameters.AddWithValue("fields",
                """{"firstName":"Alice","lastName":"Smith"}""");
            var bkeysParam = new NpgsqlParameter("bkeys",
                NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = new string[] { "alice", "smith" }
            };
            recCmd.Parameters.Add(bkeysParam);
            recCmd.Parameters.AddWithValue("cid", clusterId);
            recCmd.Parameters.AddWithValue("cat", DateTime.UtcNow);
            await recCmd.ExecuteNonQueryAsync();
        }

        // ── ListEntityRecordsAsync: Fields (jsonb) + BlockingKeys (text[]) ───────
        var records = await store.ListEntityRecordsAsync(project.Id);
        var rec = Assert.Single(records, r => r.Id == recordId);

        Assert.True(rec.Fields.ContainsKey("firstName"),  "Fields dict missing 'firstName'");
        Assert.Equal("Alice",  rec.Fields["firstName"]);
        Assert.Equal("Smith",  rec.Fields["lastName"]);

        Assert.Equal(2, rec.BlockingKeys.Count);
        Assert.Equal("alice", rec.BlockingKeys[0]);
        Assert.Equal("smith", rec.BlockingKeys[1]);

        // ── ListClustersAsync: MemberEntityRecordIds hydration ───────────────────
        var clusters = await store.ListClustersAsync(project.Id);
        var cluster = Assert.Single(clusters, c => c.Id == clusterId);

        Assert.Contains(recordId, cluster.MemberEntityRecordIds);
        Assert.Equal("active", cluster.Status);
    }

    [SkippableFact]
    public async Task ValidationErrors_MatchFileMetadataStore_Behaviour()
    {
        Skip.IfNot(DockerProbe.IsAvailable(), "Docker not available — skipping Testcontainers test");

        var store = _store!;

        var project = await store.CreateProjectAsync("ValidationProject", "person", null, DateTimeOffset.UtcNow);

        // Duplicate project name
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateProjectAsync("ValidationProject", "person", null, DateTimeOffset.UtcNow));

        // Source on non-existent project
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateSourceAsync(Guid.NewGuid(), "S", DateTimeOffset.UtcNow));

        var source = await store.CreateSourceAsync(project.Id, "ValSrc", DateTimeOffset.UtcNow);

        // Duplicate source name
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateSourceAsync(project.Id, "ValSrc", DateTimeOffset.UtcNow));

        // Batch on non-existent project
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateIngestBatchAsync(Guid.NewGuid(), source.Id, null, 1, DateTimeOffset.UtcNow));

        // Batch with source not belonging to project
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateIngestBatchAsync(project.Id, Guid.NewGuid(), null, 1, DateTimeOffset.UtcNow));
    }
}
