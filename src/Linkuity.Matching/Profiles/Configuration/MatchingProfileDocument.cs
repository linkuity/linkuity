namespace Linkuity.Matching.Profiles.Configuration;

/// <summary>System.Text.Json shape of a matching-profile config file. Every
/// member is nullable so a missing required value is detected by the loader
/// rather than silently defaulted.</summary>
public sealed class MatchingProfileDocument
{
    public string? ContentType { get; init; }
    public List<MatchingProfileFieldDocument>? Fields { get; init; }
    public string? NormalizationStrategy { get; init; }
    public List<string>? BlockingStrategies { get; init; }
    public string? CandidateRetrievalStrategy { get; init; }
    public string? SimilarityStrategy { get; init; }
    public string? ScoringStrategy { get; init; }
    public string? DecisionStrategy { get; init; }
    public string? ClusteringStrategy { get; init; }
    public double? AutoMatchThreshold { get; init; }
    public double? ReviewThreshold { get; init; }
    public double? ReviewFloorGate { get; init; }
}

public sealed class MatchingProfileFieldDocument
{
    public string? Name { get; init; }
    public string? SemanticType { get; init; }
    public List<string>? Roles { get; init; }
    public string? SimilarityEvaluator { get; init; }
    public double? Weight { get; init; }
    public Dictionary<string, string>? EvaluatorOptions { get; init; }
}
