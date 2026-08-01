using Npgsql;

namespace Linkuity.TestSupport;

/// <summary>
/// Creates isolated databases on an already-running Postgres, whether that is a Testcontainer
/// or an external instance. A database is cheap where a container is not, so this is how test
/// classes get isolation without each asking Docker for a server of its own.
/// </summary>
public static class PostgresDatabaseFactory
{
    /// <summary>
    /// Creates a fresh database and returns a connection string pointing at it.
    /// <paramref name="label"/> only makes the database recognisable when inspecting a failed
    /// run; uniqueness comes from a random suffix, so concurrent callers cannot collide.
    /// </summary>
    public static async Task<string> CreateDatabaseAsync(string adminConnectionString, string label)
    {
        var name = UniqueName(label);

        await using (var admin = new NpgsqlConnection(adminConnectionString))
        {
            await admin.OpenAsync();
            // The identifier is built from a sanitized label and a generated suffix, never from
            // test data, so there is nothing here for a caller to inject.
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await cmd.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = name }.ConnectionString;
    }

    /// <summary>
    /// Postgres lowercases unquoted identifiers and caps them at 63 bytes, so the label is
    /// reduced to safe characters and truncated — a class name can be passed in directly.
    /// </summary>
    private static string UniqueName(string label)
    {
        var cleaned = new string(label.Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();
        if (cleaned.Length == 0)
            cleaned = "test";
        if (cleaned.Length > 30)
            cleaned = cleaned[..30];
        return $"{cleaned}_{Guid.NewGuid():N}"[..Math.Min(62, cleaned.Length + 33)];
    }
}
