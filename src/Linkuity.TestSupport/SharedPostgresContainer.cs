using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Linkuity.TestSupport;

/// <summary>
/// One Postgres container per test assembly, handing each test class its own database.
///
/// Every container-backed test class used to start a container of its own. xUnit runs test
/// classes in parallel and `dotnet test` runs assemblies in parallel, so a full-solution run
/// asked Docker for eight `postgres:16-alpine` containers at once, each booting and migrating
/// simultaneously. Under that load container startup intermittently failed, and whole test
/// classes failed together — reliably green in isolation, which is the signature of contention
/// rather than a defect in the code under test.
///
/// A database is cheap where a container is not: one startup replaces five, so this is faster
/// as well as steadier. Isolation is preserved because no two classes share a database.
///
/// Classes using this must share one xUnit collection, which also stops them competing for the
/// same daemon at the same moment.
/// </summary>
public sealed class SharedPostgresContainer : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    /// <summary>
    /// False when Docker is absent, in which case the container was never started and tests are
    /// expected to skip. Distinct from "Docker was slow" — see <see cref="DockerProbe"/>.
    /// </summary>
    public bool IsAvailable { get; private set; }

    public async Task InitializeAsync()
    {
        if (!DockerProbe.IsAvailable())
            return;

        _container = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .Build();

        await _container.StartAsync();
        IsAvailable = true;
    }

    /// <summary>
    /// Creates a fresh database and returns its connection string. <paramref name="label"/> only
    /// makes the database name recognisable when inspecting a failed run; uniqueness comes from
    /// the counter.
    /// </summary>
    public Task<string> CreateDatabaseAsync(string label)
        => _container is null
            ? throw new InvalidOperationException(
                "Postgres container is not running. Guard the test with Skip.IfNot(shared.IsAvailable, ...).")
            : PostgresDatabaseFactory.CreateDatabaseAsync(_container.GetConnectionString(), label);

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
