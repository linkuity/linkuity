namespace Linkuity.Matching.Strategies;

/// <summary>
/// A single raw similarity signal between two records (e.g. "exact:email" = 1.0).
/// <para>
/// <see cref="Value"/> is meaningful only when <see cref="Outcome"/> is
/// <see cref="ComparisonOutcome.Compared"/>; it is 0 otherwise and must not be read as
/// disagreement. The default keeps aggregate-shape strategies and existing call sites unchanged.
/// </para>
/// </summary>
/// <param name="Level">
/// Which level of a <see cref="Profiles.ProfileComparison"/> the pair landed in, when
/// <see cref="Name"/> is a comparison rather than a field. Null for an ordinary per-field signal,
/// which is every signal a profile declaring no comparisons produces. The scorer prices a level
/// from the level's own m and u, so this is not decoration: without it the signal cannot be
/// priced at all.
/// </param>
public sealed record SimilaritySignal(
    string Name,
    double Value,
    ComparisonOutcome Outcome = ComparisonOutcome.Compared,
    string? Level = null);
