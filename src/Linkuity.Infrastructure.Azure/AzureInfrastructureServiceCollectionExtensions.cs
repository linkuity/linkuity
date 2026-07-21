using Linkuity.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Linkuity.Infrastructure.Azure;

public static class AzureInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddAzureApiInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<BlobStorageOptions>(configuration.GetSection("BlobStorage"));
        services.AddSingleton<IBlobStore, AzureBlobStore>();
        return services;
    }
}
