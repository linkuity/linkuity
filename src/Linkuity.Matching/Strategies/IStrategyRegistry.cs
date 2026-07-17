namespace Linkuity.Matching.Strategies;

/// <summary>
/// Central registry exposing the eight strategy categories, each keyed by
/// strategy name. Matching profiles select strategies by name.
/// </summary>
public interface IStrategyRegistry
{
    IReadOnlyDictionary<string, INormalizationStrategy> Normalization { get; }
    IReadOnlyDictionary<string, IBlockingStrategy> Blocking { get; }
    IReadOnlyDictionary<string, ICandidateRetrievalStrategy> CandidateRetrieval { get; }
    IReadOnlyDictionary<string, ISimilarityStrategy> Similarity { get; }
    IReadOnlyDictionary<string, IScoringStrategy> Scoring { get; }
    IReadOnlyDictionary<string, IDecisionStrategy> Decision { get; }
    IReadOnlyDictionary<string, IClusteringStrategy> Clustering { get; }
    IReadOnlyDictionary<string, ISimilarityEvaluator> Evaluators { get; }
}
