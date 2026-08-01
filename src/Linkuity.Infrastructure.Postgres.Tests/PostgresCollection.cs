using Linkuity.TestSupport;

namespace Linkuity.Infrastructure.Postgres.Tests;

/// <summary>
/// Puts every container-backed class in this assembly into one xUnit collection sharing a
/// single Postgres container. Two effects, both wanted: one container start instead of one per
/// class (or, in the incremental-ingest tests, one per test), and no two of these classes
/// running at the same instant to compete for the daemon.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<SharedPostgresContainer>
{
    public const string Name = "postgres";
}
