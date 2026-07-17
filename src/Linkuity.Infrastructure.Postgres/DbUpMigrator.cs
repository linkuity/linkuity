using DbUp;
using DbUp.Engine;

namespace Linkuity.Infrastructure.Postgres;

public static class DbUpMigrator
{
    public static void EnsureSchema(string connectionString)
    {
        // DbUp's schema upgrade below creates the tables but not the database itself, so the
        // target database must exist first. Create it if it is missing (connecting to the
        // server's default maintenance database), so pointing at a bare PostgreSQL server just
        // works. Silence DbUp's console output while we do it — the CLI's stdout carries CSV
        // read-back and must stay clean.
        TextWriter previousOut = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            EnsureDatabase.For.PostgresqlDatabase(connectionString);
        }
        finally
        {
            Console.SetOut(previousOut);
        }

        UpgradeEngine upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(typeof(DbUpMigrator).Assembly)
            .JournalToPostgresqlTable("public", "schema_versions")
            .LogToNowhere()
            .Build();

        DatabaseUpgradeResult result = upgrader.PerformUpgrade();

        if (!result.Successful)
        {
            throw new InvalidOperationException(
                $"DbUp migration failed: {result.Error?.Message}", result.Error);
        }
    }
}
