using Linkuity.Core.Interfaces;
using Linkuity.Core.Runtime;
using Linkuity.Infrastructure.Azure;
using Linkuity.Infrastructure.Local;
using Linkuity.Infrastructure.Postgres;
using Microsoft.Extensions.Configuration;

namespace Linkuity.Api.Infrastructure;

public static class RuntimeInfrastructureRegistration
{
    public static IServiceCollection AddRuntimeInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var runtimeMode = configuration.GetValue<RuntimeMode?>("Linkuity:RuntimeMode") ?? RuntimeMode.Local;
        var metadataKind = configuration.GetValue<MetadataStoreKind?>("Linkuity:MetadataStore") ?? MetadataStoreKind.File;

        if (metadataKind == MetadataStoreKind.Postgres)
        {
            services.AddLinkuityPostgres(configuration);
        }
        else
        {
            var metadataPath = configuration.GetValue<string>("MetadataStorage:DatabasePath") ?? ".linkuity/metadata/linkuity.json";
            services.AddSingleton<IMetadataStore>(_ =>
                new FileMetadataStore(new FileMetadataStoreOptions
                {
                    DatabasePath = metadataPath
                }));
        }

        if (runtimeMode == RuntimeMode.Azure)
        {
            services.AddAzureApiInfrastructure(configuration);
        }
        else
        {
            var artifactRoot = configuration.GetValue<string>("ArtifactStorage:RootPath") ?? ".linkuity/jobs";
            services.AddSingleton<IBlobStore>(_ =>
                new FileSystemArtifactStore(new FileSystemArtifactStoreOptions
                {
                    RootPath = artifactRoot
                }));
            services.AddSingleton<IJobDispatcher, NoOpJobDispatcher>();
        }

        return services;
    }
}
