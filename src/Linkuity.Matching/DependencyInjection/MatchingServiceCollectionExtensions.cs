using Linkuity.Matching.Profiles;
using Linkuity.Matching.Profiles.Configuration;
using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;
using Microsoft.Extensions.DependencyInjection;

namespace Linkuity.Matching.DependencyInjection;

public static class MatchingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the strategy registry, the default strategies, the built-in
    /// person and organization matching profiles (overridable via
    /// <see cref="LinkuityMatchingOptions.LoadProfilesFrom"/>), the profile
    /// provider, and the matching engine.
    /// </summary>
    public static IServiceCollection AddLinkuityMatchingDefaults(
        this IServiceCollection services,
        Action<LinkuityMatchingOptions>? configure = null)
    {
        services.AddSingleton<INormalizationStrategy, SemanticFieldNormalizationStrategy>();
        services.AddSingleton<INormalizationStrategy, IdentityNormalizationStrategy>();

        services.AddSingleton<IBlockingStrategy, ExactValueBlockingStrategy>();
        services.AddSingleton<IBlockingStrategy, TokenNameBlockingStrategy>();
        services.AddSingleton<IBlockingStrategy, PrefixBlockingStrategy>();
        services.AddSingleton<IBlockingStrategy, NGramBlockingStrategy>();
        services.AddSingleton<IBlockingStrategy, PhoneticBlockingStrategy>();
        services.AddSingleton<IBlockingStrategy>(_ => CompositeBlockingStrategy.DobLastNamePhonetic());

        services.AddSingleton<ICandidateRetrievalStrategy, LinearCandidateRetrievalStrategy>();
        services.AddSingleton<ICandidateRetrievalStrategy, BlockingAwareLinearRetrievalStrategy>();
        services.AddSingleton<ISimilarityStrategy, DefaultSimilarityStrategy>();
        services.AddSingleton<IScoringStrategy, DefaultScoringStrategy>();
        services.AddSingleton<IScoringStrategy, WeightedScoringStrategy>();
        services.AddSingleton<IScoringStrategy, IdentifierAwareWeightedScoringStrategy>();
        services.AddSingleton<IDecisionStrategy, ThresholdDecisionStrategy>();
        services.AddSingleton<IClusteringStrategy, UnionFindClusteringStrategy>();

        services.AddSingleton<ISimilarityEvaluator, ExactSimilarityEvaluator>();
        services.AddSingleton<ISimilarityEvaluator, FuzzyTextSimilarityEvaluator>();
        services.AddSingleton<ISimilarityEvaluator, JaccardSimilarityEvaluator>();
        services.AddSingleton<ISimilarityEvaluator, NGramSimilarityEvaluator>();
        services.AddSingleton<ISimilarityEvaluator, NumericSimilarityEvaluator>();
        services.AddSingleton<ISimilarityEvaluator, DateSimilarityEvaluator>();
        services.AddSingleton<ISimilarityStrategy>(sp => new WeightedFieldSimilarityStrategy(sp.GetServices<ISimilarityEvaluator>()));

        services.AddSingleton<IStrategyRegistry>(sp => new DefaultStrategyRegistry(
            sp.GetServices<INormalizationStrategy>(),
            sp.GetServices<IBlockingStrategy>(),
            sp.GetServices<ICandidateRetrievalStrategy>(),
            sp.GetServices<ISimilarityStrategy>(),
            sp.GetServices<IScoringStrategy>(),
            sp.GetServices<IDecisionStrategy>(),
            sp.GetServices<IClusteringStrategy>(),
            sp.GetServices<ISimilarityEvaluator>()));

        var options = new LinkuityMatchingOptions();
        configure?.Invoke(options);

        services.AddSingleton<IMatchingProfileProvider>(sp =>
        {
            var registry = sp.GetRequiredService<IStrategyRegistry>();
            var loader = new MatchingProfileConfigLoader();
            var loaded = options.ProfilePaths
                .SelectMany(path => Directory.Exists(path)
                    ? loader.LoadFromDirectory(path, registry)
                    : (IEnumerable<MatchingProfile>)[loader.LoadFromFile(path, registry)])
                .ToList();
            return new DefaultMatchingProfileProvider(
                DefaultMatchingProfileProvider.BuiltInProfiles(), loaded);
        });

        services.AddSingleton<IMatchingEngine, MatchingEngine>();

        return services;
    }
}
