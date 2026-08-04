using Linkuity.TestSupport;
using Npgsql;

namespace Linkuity.Infrastructure.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class SchemaMigrationTests(SharedPostgresContainer shared)
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
        "cluster_dissolution_events",
    ];

    [SkippableFact]
    public async Task EnsureSchema_CreatesAllElevenTables()
    {
        Skip.IfNot(shared.IsAvailable, "Docker not available — skipping Testcontainers test");

        // A fresh database, not a fresh container: this asserts the migration creates every
        // table in an empty database, which a new database satisfies exactly as well.
        string connectionString = await shared.CreateDatabaseAsync(nameof(SchemaMigrationTests));

        // Run migration
        DbUpMigrator.EnsureSchema(connectionString);

        // Verify all 11 tables exist
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
