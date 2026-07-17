using Linkuity.TestSupport;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Linkuity.Infrastructure.Postgres.Tests;

public sealed class SchemaMigrationTests
{
    private static readonly string[] ExpectedTables =
    [
        "projects",
        "sources",
        "ingest_batches",
        "entity_records",
        "match_edges",
        "clusters",
        "golden_records",
        "golden_record_versions",
        "review_tasks",
        "cluster_merge_events",
    ];

    [SkippableFact]
    public async Task EnsureSchema_CreatesAllTenTables()
    {
        Skip.IfNot(DockerProbe.IsAvailable(), "Docker not available — skipping Testcontainers test");

        await using var pg = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .Build();

        await pg.StartAsync();

        string connectionString = pg.GetConnectionString();

        // Run migration
        DbUpMigrator.EnsureSchema(connectionString);

        // Verify all 10 tables exist
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        var foundTables = new List<string>();
        await using var cmd = new NpgsqlCommand(
            """
            select table_name
            from information_schema.tables
            where table_schema = 'public'
              and table_type = 'BASE TABLE'
              and table_name != 'schema_versions'
            order by table_name
            """,
            conn);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            foundTables.Add(reader.GetString(0));
        }

        foreach (string expected in ExpectedTables)
        {
            Assert.Contains(expected, foundTables);
        }

        Assert.Equal(ExpectedTables.Length, foundTables.Count);
    }
}
