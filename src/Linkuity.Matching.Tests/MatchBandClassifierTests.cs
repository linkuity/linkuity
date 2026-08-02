namespace Linkuity.Matching.Tests;

/// <summary>
/// The single seam replacing six independent band implementations. Its boundaries must match
/// what <c>DecisionBandCharacterizationTests</c> and <c>CorpusAuditBandCharacterizationTests</c>
/// pinned, or consolidation has silently changed an outcome.
/// </summary>
public class MatchBandClassifierTests
{
    private static readonly MatchThresholds Thresholds = new(autoMatch: 0.90, review: 0.75);

    [Theory]
    [InlineData(1.00, MatchDecision.AutoMatch)]
    [InlineData(0.90, MatchDecision.AutoMatch)]   // inclusive at the auto threshold
    [InlineData(0.8999, MatchDecision.Review)]
    [InlineData(0.75, MatchDecision.Review)]      // inclusive at the review threshold
    [InlineData(0.7499, MatchDecision.NoMatch)]
    [InlineData(0.00, MatchDecision.NoMatch)]
    public void ComparablePair_MatchesTheBoundariesTheOldImplementationsUsed(double score, MatchDecision expected)
        => Assert.Equal(expected, MatchBandClassifier.Classify(score, comparable: true, Thresholds));

    /// <summary>
    /// The band the old production implementations could not express. The score is ignored
    /// entirely — a non-comparable pair is not "low scoring", its score means nothing.
    /// </summary>
    [Theory]
    [InlineData(1.00)]
    [InlineData(0.80)]
    [InlineData(0.00)]
    public void NonComparablePair_IsNeverBandedByScore(double score)
        => Assert.Equal(MatchDecision.NonComparable, MatchBandClassifier.Classify(score, comparable: false, Thresholds));

    // ── Thresholds validate on construction ──────────────────────────────────────

    [Fact]
    public void EqualThresholds_Rejected_BecauseTheReviewBandWouldBeEmpty()
        => Assert.Throws<ArgumentException>(() => new MatchThresholds(autoMatch: 0.8, review: 0.8));

    [Fact]
    public void InvertedThresholds_Rejected()
        => Assert.Throws<ArgumentException>(() => new MatchThresholds(autoMatch: 0.5, review: 0.9));

    [Theory]
    [InlineData(1.5, 0.75)]
    [InlineData(0.9, -0.1)]
    public void OutOfRangeOnUnitInterval_Rejected(double auto, double review)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new MatchThresholds(auto, review, ScoreScale.UnitInterval));

    [Theory]
    [InlineData(double.NaN, 0.75)]
    [InlineData(0.9, double.NaN)]
    [InlineData(double.PositiveInfinity, 0.75)]
    public void NonFiniteThresholds_Rejected(double auto, double review)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new MatchThresholds(auto, review));

    /// <summary>
    /// The point of carrying a scale: thresholds outside [0,1] are a configuration error on a
    /// normalized scorer and entirely ordinary on a log-odds one, where a threshold of 8.0 means
    /// "roughly 3,000:1 odds in favour". The same numbers must be rejected on one and accepted
    /// on the other.
    /// </summary>
    [Fact]
    public void SameThresholds_RejectedOnUnitInterval_AcceptedOnLogOdds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MatchThresholds(8.0, 2.0, ScoreScale.UnitInterval));

        var logOdds = new MatchThresholds(8.0, 2.0, ScoreScale.LogOdds);

        Assert.Equal(MatchDecision.AutoMatch, MatchBandClassifier.Classify(9.1, comparable: true, logOdds));
        Assert.Equal(MatchDecision.Review, MatchBandClassifier.Classify(2.0, comparable: true, logOdds));
        // Negative evidence is ordinary on this scale and must not be treated as an error.
        Assert.Equal(MatchDecision.NoMatch, MatchBandClassifier.Classify(-4.2, comparable: true, logOdds));
    }
}
