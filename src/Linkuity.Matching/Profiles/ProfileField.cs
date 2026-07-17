using Linkuity.Core.Models;

namespace Linkuity.Matching.Profiles;

public sealed class ProfileField
{
    public required string Name { get; init; }
    public required SemanticFieldType SemanticType { get; init; }
    public required FieldRole Roles { get; init; }

    /// <summary>Name of the similarity evaluator for this field (consumed in Milestone 13).</summary>
    public string? SimilarityEvaluator { get; init; }

    /// <summary>Per-field scoring weight (consumed in Milestone 13). Defaults to 1.0.</summary>
    public double Weight { get; init; } = 1.0;

    /// <summary>
    /// Optional per-field evaluator configuration (consumed by the numeric, date,
    /// and n-gram evaluators). Documented keys: "numeric.tolerance",
    /// "numeric.maxPercentDiff", "date.maxDays", "ngram.size". Defaults to null.
    /// </summary>
    public IReadOnlyDictionary<string, string>? EvaluatorOptions { get; init; }
}
