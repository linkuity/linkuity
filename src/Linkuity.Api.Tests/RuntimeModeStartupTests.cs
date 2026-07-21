using Linkuity.Api.Infrastructure;
using Linkuity.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Linkuity.Api.Tests;

public sealed class RuntimeModeStartupTests
{
    [Fact]
    public void DefaultRuntimeMode_ResolvesLocalInfrastructureWithoutAzureConfiguration()
    {
        var services = BuildServices(new Dictionary<string, string?>());

        var blobs = services.GetRequiredService<IBlobStore>();

        Assert.DoesNotContain("Azure", blobs.GetType().FullName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AzureRuntimeMode_ResolvesAzureAdapterInfrastructure()
    {
        var services = BuildServices(new Dictionary<string, string?>
        {
            ["Linkuity:RuntimeMode"] = "Azure",
            ["BlobStorage:ConnectionString"] = "UseDevelopmentStorage=true",
            ["BlobStorage:ContainerName"] = "linkuity-jobs"
        });

        var blobs = services.GetRequiredService<IBlobStore>();

        Assert.Contains("Azure", blobs.GetType().FullName, StringComparison.OrdinalIgnoreCase);
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
