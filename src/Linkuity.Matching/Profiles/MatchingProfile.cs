namespace Linkuity.Matching.Profiles;

/// <summary>
/// Metadata-driven matching configuration selected per content type. Names
/// reference strategies registered in the <see cref="Strategies.IStrategyRegistry"/>.
/// </summary>
public sealed class MatchingProfile
{
    public required string ContentType { get; init; }
    public required IReadOnlyList<ProfileField> Fields { get; init; }

    public required string NormalizationStrategy { get; init; }
    public required IReadOnlyList<string> BlockingStrategies { get; init; }
    public required string CandidateRetrievalStrategy { get; init; }
    public required string SimilarityStrategy { get; init; }
    public required string ScoringStrategy { get; init; }
    public required string DecisionStrategy { get; init; }
    public required string ClusteringStrategy { get; init; }

    public required double AutoMatchThreshold { get; init; }
    public required double ReviewThreshold { get; init; }

    /// <summary>
    /// Minimum weighted per-field similarity a non-identifier candidate must reach for the review
    /// floor (0.80) to apply. Below this the raw weighted score stands (typically a NoMatch), which
    /// stops a shared low-cardinality blocking key alone from flooding the review queue. Default 0.75.
    /// Lower it below <see cref="ReviewThreshold"/> to promote strongly-evidenced sub-threshold pairs
    /// into review. Consumed by <c>IdentifierAwareWeightedScoringStrategy</c>.
    /// </summary>
    public double ReviewFloorGate { get; init; } = 0.75;
}
