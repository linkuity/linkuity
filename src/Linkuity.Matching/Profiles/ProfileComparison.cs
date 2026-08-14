namespace Linkuity.Matching.Profiles;

/// <summary>
/// Several fields compared ONCE, as a ladder of ordered levels, because they are facets of one
/// fact rather than independent facts.
///
/// Street, postcode, city, region and country are nested: a pair agreeing on the street almost
/// always agrees on the country too. Scored as five independent fields they contribute five times
/// for one fact, which inflates the evidence in both directions — agreement on an address becomes
/// nearly enough to merge on its own, and disagreement becomes an override no name can survive.
/// Measured on the GLEIF corpus, five location fields summed to 11.4 bits against an auto-match
/// bar of 13.3, so a 40%-similar name at an identical address auto-merged.
///
/// The fix is not to average them or take the strongest — averaging tolerates the duplication and
/// "strongest" lets an agreeing city override a differing street. It is to ask ONE question with
/// ordered answers: same street, else same postcode, else same city, else same region, else same
/// country, else nothing. Each answer carries its own measured m and u, and a pair contributes
/// exactly one of them. This is how Splink models nested attributes and how address hierarchy is
/// handled in entity resolution generally.
///
/// A comparison's member fields are NOT also scored individually; the similarity strategy emits
/// one signal for the comparison and none for its members, which is what makes "one fact, one
/// contribution" true rather than aspirational.
/// </summary>
public sealed record ProfileComparison
{
    /// <summary>
    /// Identifies the comparison in signals and score breakdowns. Must not collide with a field
    /// name — the scorer resolves a signal by name and would otherwise price it as a field.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The fields this comparison consumes. Declared explicitly rather than inferred from the
    /// levels, so what a comparison takes over is legible without reading every level, and so a
    /// field can participate in deciding evaluability without appearing in any requirement.
    /// </summary>
    public required IReadOnlyList<string> Fields { get; init; }

    /// <summary>
    /// Ordered, most specific first. The first level whose requirements are all met wins; the last
    /// level must have no requirements, so the ladder is exhaustive and every evaluable pair
    /// receives exactly one answer.
    /// </summary>
    public required IReadOnlyList<ComparisonLevel> Levels { get; init; }
}

/// <summary>
/// One rung of a <see cref="ProfileComparison"/>: a set of requirements that must all hold, and
/// what a pair meeting them is worth.
/// </summary>
public sealed record ComparisonLevel
{
    /// <summary>Names this level in the score breakdown ("same-postcode", "same-city", "none").</summary>
    public required string Name { get; init; }

    /// <summary>
    /// All must hold for the level to match. Empty means the level always matches, which is
    /// required of the last level and rejected on any other — an earlier catch-all would make
    /// every level below it unreachable.
    /// </summary>
    public required IReadOnlyList<LevelRequirement> Requirements { get; init; }

    /// <summary>What a pair landing in this level contributes.</summary>
    public required LevelEvidence Evidence { get; init; }
}

/// <summary>
/// One field of a level's test: that field must be comparable on both sides and reach at least
/// <see cref="MinSimilarity"/>.
/// </summary>
public sealed record LevelRequirement
{
    public required string Field { get; init; }

    /// <summary>
    /// Defaults to 1.0 — exact agreement. Below 1.0 only where the field is genuinely graded; a
    /// street line compared by token overlap is the case this exists for.
    /// </summary>
    public double MinSimilarity
    {
        get;
        init
        {
            if (double.IsNaN(value) || value <= 0 || value > 1)
                throw new ArgumentOutOfRangeException(nameof(MinSimilarity), value,
                    "minSimilarity must be greater than 0 and at most 1.");
            field = value;
        }
    } = 1.0;
}
