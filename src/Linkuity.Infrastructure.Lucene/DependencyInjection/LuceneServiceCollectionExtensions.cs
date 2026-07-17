using Linkuity.Matching.Strategies;
using Microsoft.Extensions.DependencyInjection;

namespace Linkuity.Infrastructure.Lucene.DependencyInjection;

/// <summary>
/// Opt-in registration for the Lucene candidate-retrieval adapter. Kept separate
/// from AddLinkuityMatchingDefaults so the heavy Lucene dependency is pulled in only
/// when a host explicitly wants index-backed retrieval. Milestone 16 selects the
/// "lucene" strategy via a profile and drives the index lifecycle through the
/// IIndexedCandidateRetrievalStrategy seam resolved here.
/// </summary>
public static class LuceneServiceCollectionExtensions
{
    public static IServiceCollection AddLinkuityLuceneRetrieval(this IServiceCollection services, string indexDirectory)
        => services.AddLinkuityLuceneRetrieval(new LuceneCandidateRetrievalOptions { IndexDirectory = indexDirectory });

    public static IServiceCollection AddLinkuityLuceneRetrieval(this IServiceCollection services, LuceneCandidateRetrievalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.AddSingleton<LuceneCandidateRetrieval>(sp => new LuceneCandidateRetrieval(sp.GetRequiredService<LuceneCandidateRetrievalOptions>()));
        services.AddSingleton<IIndexedCandidateRetrievalStrategy>(sp => sp.GetRequiredService<LuceneCandidateRetrieval>());
        services.AddSingleton<ICandidateRetrievalStrategy>(sp => sp.GetRequiredService<LuceneCandidateRetrieval>());

        return services;
    }
}
