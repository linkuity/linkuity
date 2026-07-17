using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Linkuity.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Linkuity.Infrastructure.Azure;

public static class AzureInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddAzureApiInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<BlobStorageOptions>(configuration.GetSection("BlobStorage"));
        services.Configure<ServiceBusOptions>(configuration.GetSection("AzureServiceBus"));
        services.AddSingleton<IBlobStore, AzureBlobStore>();
        services.AddSingleton<IJobDispatcher, AzureServiceBusJobDispatcher>();
        return services;
    }

    public static IServiceCollection AddAzurePostProcessingWorker(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<BlobStorageOptions>(configuration.GetSection("BlobStorage"));
        services.Configure<ServiceBusOptions>(configuration.GetSection("AzureServiceBus"));
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ServiceBusOptions>>().Value;
            return new ServiceBusClient(options.ConnectionString);
        });
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<BlobStorageOptions>>().Value;
            return new BlobServiceClient(options.ConnectionString);
        });
        services.AddSingleton<IArtifactStore>(sp =>
        {
            var blobClient = sp.GetRequiredService<BlobServiceClient>();
            var options = sp.GetRequiredService<IOptions<BlobStorageOptions>>().Value;
            return new AzureBlobArtifactStore(blobClient, options.ContainerName);
        });
        services.AddHostedService<AzurePostProcessingWorkerService>();
        return services;
    }
}
