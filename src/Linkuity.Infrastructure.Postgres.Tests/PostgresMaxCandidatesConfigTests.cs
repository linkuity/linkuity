using Linkuity.Infrastructure.Lucene;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Linkuity.Infrastructure.Postgres.Tests;

public sealed class PostgresMaxCandidatesConfigTests
{
    [Fact]
    public void AddLinkuityPostgres_BindsMaxCandidatesFromConfig()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Linkuity:Postgres:ConnectionString"] = "Host=localhost;Database=x;Username=u;Password=p",
                ["Linkuity:Postgres:IndexDirectory"] = Path.Combine(Path.GetTempPath(), "m19-cfg-" + Guid.NewGuid().ToString("N")),
                ["Linkuity:Postgres:MaxCandidates"] = "17",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLinkuityPostgres(config);
        using var sp = services.BuildServiceProvider();

        // Resolving the options singleton does not open a DB connection.
        var options = sp.GetRequiredService<LuceneCandidateRetrievalOptions>();
        Assert.Equal(17, options.MaxCandidates);
    }
}
