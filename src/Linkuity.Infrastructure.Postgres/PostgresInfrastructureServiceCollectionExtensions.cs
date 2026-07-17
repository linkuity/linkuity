using Linkuity.Core.Interfaces;
using Linkuity.Infrastructure.Lucene;
using Linkuity.Infrastructure.Lucene.DependencyInjection;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Linkuity.Infrastructure.Postgres;

public static class PostgresInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddLinkuityPostgres(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration["Linkuity:Postgres:ConnectionString"]
            ?? throw new InvalidOperationException("Linkuity:Postgres:ConnectionString is required for the Postgres metadata store.");
        var indexDirectory = configuration["Linkuity:Postgres:IndexDirectory"] ?? ".linkuity/lucene-index";
        var maxCandidates = int.TryParse(configuration["Linkuity:Postgres:MaxCandidates"], out var mc) && mc > 0 ? mc : 50;

        var storeOptions = new PostgresMetadataStoreOptions { ConnectionString = connectionString };

        // Postgres ingest uses index-backed candidate retrieval (hasIndex:true resolver path).
        // Without an index the resolver falls back to GetLinearCorpus which is not supported by
        // PostgresResolutionContext and will throw. Register through AddLinkuityLuceneRetrieval so
        // IIndexedCandidateRetrievalStrategy and ICandidateRetrievalStrategy aliases are also
        // registered for downstream consumers (honors the seam).
        services.AddLinkuityLuceneRetrieval(new LuceneCandidateRetrievalOptions
        {
            IndexDirectory = indexDirectory,
            MaxCandidates = maxCandidates,
        });
        services.AddSingleton<IMetadataStore>(sp =>
        {
            var luceneIndex = sp.GetRequiredService<IIndexedCandidateRetrievalStrategy>();
            var profileProvider = new DefaultMatchingProfileProvider(DefaultMatchingProfileProvider.BuiltInProfiles());
            return new PostgresMetadataStore(storeOptions, engine: null, profileProvider, luceneIndex);
        });

        return services;
    }
}
