using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching;

/// <summary>
/// Builds the default registry, profile, and engine without a DI container — used
/// by tests and non-DI callers. DI consumers use AddLinkuityMatchingDefaults.
/// </summary>
public static class MatchingDefaults
{
    public static IStrategyRegistry CreateRegistry(params ICandidateRetrievalStrategy[] additionalRetrieval)
    {
        ISimilarityEvaluator[] evaluators =
        [
            new ExactSimilarityEvaluator(),
            new FuzzyTextSimilarityEvaluator(),
            new JaccardSimilarityEvaluator(),
            new NGramSimilarityEvaluator(),
            new NumericSimilarityEvaluator(),
            new DateSimilarityEvaluator()
        ];

        return new DefaultStrategyRegistry(
            normalization: [new SemanticFieldNormalizationStrategy(), new IdentityNormalizationStrategy()],
            blocking:
            [
                new ExactValueBlockingStrategy(),
                new TokenNameBlockingStrategy(),
                new PrefixBlockingStrategy(),
                new NGramBlockingStrategy(),
                new PhoneticBlockingStrategy(),
                CompositeBlockingStrategy.DobLastNamePhonetic()
            ],
            candidateRetrieval: [new LinearCandidateRetrievalStrategy(), new BlockingAwareLinearRetrievalStrategy(), ..additionalRetrieval],
            similarity: [new DefaultSimilarityStrategy(), new WeightedFieldSimilarityStrategy(evaluators)],
            scoring: [new DefaultScoringStrategy(), new WeightedScoringStrategy(), new IdentifierAwareWeightedScoringStrategy()],
            decision: [new ThresholdDecisionStrategy()],
            clustering: [new UnionFindClusteringStrategy()],
            evaluators: evaluators);
    }

    public static MatchingProfile CreatePersonProfile() => DefaultMatchingProfileProvider.CreatePersonProfile();

    public static MatchingProfile CreateParityPersonProfile() => DefaultMatchingProfileProvider.CreateParityPersonProfile();

    public static MatchingEngine CreateEngine(params ICandidateRetrievalStrategy[] additionalRetrieval) => new(CreateRegistry(additionalRetrieval));
}
