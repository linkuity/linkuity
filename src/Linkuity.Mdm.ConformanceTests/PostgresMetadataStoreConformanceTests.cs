using Linkuity.Core.Interfaces;
using Linkuity.Infrastructure.Lucene;
using Linkuity.Infrastructure.Postgres;
using Linkuity.Matching.Profiles;
using Linkuity.TestSupport;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Linkuity.Mdm.ConformanceTests;

/// <summary>
/// Manages a single PostgreSQL Testcontainer for the entire
/// <see cref="PostgresMetadataStoreConformanceTests"/> class.
/// Shared via <c>IClassFixture</c> so the container starts once and is reused
/// across all 17 facts; per-fact isolation is achieved by truncating tables in
/// <see cref="PostgresMetadataStoreConformanceTests.CreateStoreAsync"/>.
/// </summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string? ConnectionString { get; private set; }

    public async Task InitializeAsync()
    {
        // Prefer an external local Postgres (no Docker) when LINKUITY_CONFORMANCE_POSTGRES is set;
        // fall back to a Testcontainer when it is not and Docker is available; otherwise leave
        // ConnectionString null so facts Skip.
        var external = Environment.GetEnvironmentVariable("LINKUITY_CONFORMANCE_POSTGRES");
        if (!string.IsNullOrWhiteSpace(external))
        {
            ConnectionString = await EnsureConformanceDatabaseAsync(external);
            DbUpMigrator.EnsureSchema(ConnectionString);
            return;
        }

        if (!DockerProbe.IsAvailable())
            return; // No external instance and no Docker — tests will Skip.IfNot themselves.

        _container = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .Build();

        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
        DbUpMigrator.EnsureSchema(ConnectionString);
    }

    /// <summary>
    /// Ensures a dedicated <c>linkuity_conformance</c> database exists on the external instance
    /// (created via the admin/default database if missing) and returns a connection string pointing
    /// at it, so conformance tables never land in the operator's default <c>postgres</c> database.
    /// If the supplied connection string already names a non-default database, that database is used.
    /// </summary>
    private static async Task<string> EnsureConformanceDatabaseAsync(string adminConnectionString)
    {
        const string dbName = "linkuity_conformance";
        var target = new NpgsqlConnectionStringBuilder(adminConnectionString);
        var useDedicated = string.IsNullOrWhiteSpace(target.Database) || target.Database == "postgres";

        if (useDedicated)
        {
            var admin = new NpgsqlConnectionStringBuilder(adminConnectionString);
            if (string.IsNullOrWhiteSpace(admin.Database))
                admin.Database = "postgres";

            await using var conn = new NpgsqlConnection(admin.ConnectionString);
            await conn.OpenAsync();

            await using var check = conn.CreateCommand();
            check.CommandText = "SELECT 1 FROM pg_database WHERE datname = @n";
            check.Parameters.AddWithValue("n", dbName);
            var exists = await check.ExecuteScalarAsync() is not null;

            if (!exists)
            {
                await using var create = conn.CreateCommand();
                create.CommandText = $"CREATE DATABASE \"{dbName}\"";
                await create.ExecuteNonQueryAsync();
            }

            target.Database = dbName;
        }

        return target.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}

/// <summary>
/// Runs the full <see cref="MetadataStoreConformanceTests"/> suite against
/// <see cref="PostgresMetadataStore"/>. One container is shared across all 17
/// facts (via <see cref="PostgresContainerFixture"/>); each fact gets isolation
/// via a full TRUNCATE of the 10 MDM tables and a fresh per-fact Lucene index.
/// When Docker is not available every fact is skipped (not failed).
/// </summary>
public sealed class PostgresMetadataStoreConformanceTests
    : MetadataStoreConformanceTests, IClassFixture<PostgresContainerFixture>, IDisposable
{
    private readonly PostgresContainerFixture _fixture;
    private readonly List<LuceneCandidateRetrieval> _indices = [];
    private readonly List<string> _indexDirs = [];

    public PostgresMetadataStoreConformanceTests(PostgresContainerFixture fixture)
    {
        _fixture = fixture;
    }

    protected override void SkipIfUnavailable()
    {
        Skip.If(_fixture.ConnectionString is null,
            "No Postgres available — set LINKUITY_CONFORMANCE_POSTGRES to the local instance or start Docker.");
    }

    protected override async Task<IMetadataStore> CreateStoreAsync()
    {
        // Truncate all 10 MDM tables for a clean slate before each fact.
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString!);
        await conn.OpenAsync();
        await using (var truncateCmd = conn.CreateCommand())
        {
            truncateCmd.CommandText =
                "TRUNCATE projects, sources, ingest_batches, entity_records, match_edges, " +
                "clusters, golden_records, golden_record_versions, review_tasks, cluster_merge_events CASCADE";
            await truncateCmd.ExecuteNonQueryAsync();
        }

        // Fresh per-fact Lucene index (Postgres ingest requires hasIndex:true).
        var indexDir = Path.Combine(Path.GetTempPath(), $"linkuity-pg-conf-{Guid.NewGuid():N}");
        _indexDirs.Add(indexDir);
        var index = new LuceneCandidateRetrieval(
            new LuceneCandidateRetrievalOptions { IndexDirectory = indexDir });
        _indices.Add(index);

        var profileProvider = new DefaultMatchingProfileProvider(
            DefaultMatchingProfileProvider.BuiltInProfiles());

        // Run the Postgres store's incremental ingest with parallel edge production ENABLED
        // (fixed DOP > 1 so concurrency is exercised regardless of host/CI core count). The
        // File store runs the same resolver at DOP=1, so every conformance fact that passes
        // proves parallel-Postgres produces byte-identical outcomes to sequential-File — the
        // parallel-matching correctness contract, continuously enforced.
        return new PostgresMetadataStore(
            new PostgresMetadataStoreOptions { ConnectionString = _fixture.ConnectionString!, IngestParallelism = 8 },
            engine: null,
            profileProvider,
            indexedRetrieval: index);
    }

    public void Dispose()
    {
        foreach (var index in _indices)
            index.Dispose();

        foreach (var dir in _indexDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* best-effort cleanup */ }
        }
    }
}
