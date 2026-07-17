namespace Linkuity.Matching.Strategies;

public sealed class DefaultStrategyRegistry : IStrategyRegistry
{
    public DefaultStrategyRegistry(
        IEnumerable<INormalizationStrategy> normalization,
        IEnumerable<IBlockingStrategy> blocking,
        IEnumerable<ICandidateRetrievalStrategy> candidateRetrieval,
        IEnumerable<ISimilarityStrategy> similarity,
        IEnumerable<IScoringStrategy> scoring,
        IEnumerable<IDecisionStrategy> decision,
        IEnumerable<IClusteringStrategy> clustering,
        IEnumerable<ISimilarityEvaluator> evaluators)
    {
        Normalization = Index(normalization, s => s.Name, "normalization");
        Blocking = Index(blocking, s => s.Name, "blocking");
        CandidateRetrieval = Index(candidateRetrieval, s => s.Name, "candidate-retrieval");
        Similarity = Index(similarity, s => s.Name, "similarity");
        Scoring = Index(scoring, s => s.Name, "scoring");
        Decision = Index(decision, s => s.Name, "decision");
        Clustering = Index(clustering, s => s.Name, "clustering");
        Evaluators = Index(evaluators, s => s.Name, "similarity-evaluator");
    }

    public IReadOnlyDictionary<string, INormalizationStrategy> Normalization { get; }
    public IReadOnlyDictionary<string, IBlockingStrategy> Blocking { get; }
    public IReadOnlyDictionary<string, ICandidateRetrievalStrategy> CandidateRetrieval { get; }
    public IReadOnlyDictionary<string, ISimilarityStrategy> Similarity { get; }
    public IReadOnlyDictionary<string, IScoringStrategy> Scoring { get; }
    public IReadOnlyDictionary<string, IDecisionStrategy> Decision { get; }
    public IReadOnlyDictionary<string, IClusteringStrategy> Clustering { get; }
    public IReadOnlyDictionary<string, ISimilarityEvaluator> Evaluators { get; }

    private static IReadOnlyDictionary<string, T> Index<T>(IEnumerable<T> items, Func<T, string> keySelector, string category)
    {
        var map = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var key = keySelector(item);
            if (!map.TryAdd(key, item))
                throw new ArgumentException($"Duplicate {category} strategy name: '{key}'.");
        }
        return map;
    }
}
