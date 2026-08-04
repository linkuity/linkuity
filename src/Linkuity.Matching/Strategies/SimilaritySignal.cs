namespace Linkuity.Matching.Strategies;

/// <summary>
/// A single raw similarity signal between two records (e.g. "exact:email" = 1.0).
/// <para>
/// <see cref="Value"/> is meaningful only when <see cref="Outcome"/> is
/// <see cref="ComparisonOutcome.Compared"/>; it is 0 otherwise and must not be read as
/// disagreement. The default keeps aggregate-shape strategies and existing call sites unchanged.
/// </para>
/// </summary>
public sealed record SimilaritySignal(
    string Name,
    double Value,
    ComparisonOutcome Outcome = ComparisonOutcome.Compared);
