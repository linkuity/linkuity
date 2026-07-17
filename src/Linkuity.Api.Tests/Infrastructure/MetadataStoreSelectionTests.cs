using Linkuity.Api.Infrastructure;
using Linkuity.Core.Interfaces;
using Linkuity.Infrastructure.Local;
using Linkuity.Infrastructure.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Linkuity.Api.Tests.Infrastructure;

public sealed class MetadataStoreSelectionTests
{
    [Fact]
    public void PostgresKind_ResolvesPostgresMetadataStore()
    {
        var indexDir = Path.Combine(Path.GetTempPath(), $"linkuity-lucene-test-{Guid.NewGuid()}");
        using var provider = BuildServices(new Dictionary<string, string?>
        {
            ["Linkuity:MetadataStore"] = "Postgres",
            ["Linkuity:Postgres:ConnectionString"] = "Host=localhost;Database=x",
            ["Linkuity:Postgres:IndexDirectory"] = indexDir
        });

        var store = provider.GetRequiredService<IMetadataStore>();

        Assert.IsType<PostgresMetadataStore>(store);
    }

    [Fact]
    public void FileKind_ResolvesFileMetadataStore()
    {
        using var provider = BuildServices(new Dictionary<string, string?>());

        var store = provider.GetRequiredService<IMetadataStore>();

        Assert.IsType<FileMetadataStore>(store);
    }

    [Fact]
    public void ExplicitFileKind_ResolvesFileMetadataStore()
    {
        using var provider = BuildServices(new Dictionary<string, string?>
        {
            ["Linkuity:MetadataStore"] = "File"
        });

        var store = provider.GetRequiredService<IMetadataStore>();

        Assert.IsType<FileMetadataStore>(store);
    }

    private static ServiceProvider BuildServices(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var services = new ServiceCollection();
        services.AddRuntimeInfrastructure(configuration);
        return services.BuildServiceProvider();
    }
}
