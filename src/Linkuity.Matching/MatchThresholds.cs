namespace Linkuity.Matching;

/// <summary>
/// The two thresholds that turn a score into a band, together with the scale they are
/// expressed on, validated once at construction.
///
/// Validation used to live in both metadata stores as duplicated inline checks, which meant it
/// ran only on the durable path and only for thresholds arriving through a request. Anything
/// reaching the engine another way was unchecked. Making an invalid pair unconstructible moves
/// the guarantee from "every caller remembered" to "it cannot be expressed".
/// </summary>
public readonly record struct MatchThresholds
{
    public MatchThresholds(double autoMatch, double review, ScoreScale scale = ScoreScale.UnitInterval)
    {
        if (double.IsNaN(autoMatch) || double.IsInfinity(autoMatch))
            throw new ArgumentOutOfRangeException(nameof(autoMatch), autoMatch, "autoMatch must be a finite number.");
        if (double.IsNaN(review) || double.IsInfinity(review))
            throw new ArgumentOutOfRangeException(nameof(review), review, "review must be a finite number.");

        // Equal thresholds would make the review band empty, which is far more likely to be a
        // configuration mistake than an intent to disable review.
        if (autoMatch <= review)
            throw new ArgumentException(
                $"autoMatch ({autoMatch}) must be greater than review ({review}); an empty review band is not expressible.",
                nameof(autoMatch));

        if (scale == ScoreScale.UnitInterval && (review < 0 || autoMatch > 1))
            throw new ArgumentOutOfRangeException(
                nameof(scale),
                $"thresholds {review}..{autoMatch} fall outside [0,1], which is the only meaningful range on the " +
                $"{nameof(ScoreScale.UnitInterval)} scale. A scorer producing unbounded scores must declare " +
                $"{nameof(ScoreScale.LogOdds)}.");

        AutoMatch = autoMatch;
        Review = review;
        Scale = scale;
    }

    public double AutoMatch { get; }
    public double Review { get; }
    public ScoreScale Scale { get; }
}
