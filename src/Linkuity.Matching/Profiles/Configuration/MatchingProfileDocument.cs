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
    public double? IdentifierFloorGate { get; init; }
    public int? MaxBlockSize { get; init; }
    public string? DefaultPhoneRegion { get; init; }
    public string? DefaultDateOrder { get; init; }
    public List<string>? RarityExemptValues { get; init; }
    public double? MinClusterCohesion { get; init; }
    public int? MaxAutoClusterSize { get; init; }
}

public sealed class MatchingProfileFieldDocument
{
    public string? Name { get; init; }
    public string? SemanticType { get; init; }
    public List<string>? Roles { get; init; }
    public string? SimilarityEvaluator { get; init; }
    public double? Weight { get; init; }
    public Dictionary<string, string>? EvaluatorOptions { get; init; }
    public FieldEvidenceDocument? Evidence { get; init; }
    public string? AliasGroup { get; init; }
    public List<string>? NullEquivalents { get; init; }

    /// <summary>Field this one's value is derived from. Declared with <see cref="Extractor"/>.</summary>
    public string? SourceField { get; init; }

    /// <summary>Named value extractor applied to <see cref="SourceField"/>. Declared with it.</summary>
    public string? Extractor { get; init; }
}

public sealed class FieldEvidenceDocument
{
    public double? SameEntityAgreement { get; init; }
    public double? ChanceAgreement { get; init; }
    public double? MaxAgreementBits { get; init; }
}
