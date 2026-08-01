namespace Linkuity.Matching;

/// <summary>
/// The single place a score becomes a band.
///
/// Six independent implementations did this, and they disagreed on how many bands exist: batch
/// recognised two, the durable path three, and the two audits four. The audits were the most
/// truthful, and this adopts their model.
///
/// The fourth band carries information the other implementations threw away. A pair that
/// produced no signals was not "compared and found wanting" — nothing was compared, so its
/// score is meaningless rather than low. Reporting that as no-match states a conclusion the
/// evidence does not support. The distinction becomes load-bearing under evidence scoring,
/// where a field neither record populates contributes no evidence rather than dragging an
/// average down.
/// </summary>
public static class MatchBandClassifier
{
    /// <param name="score">The scorer's output, on <paramref name="thresholds"/>'s scale.</param>
    /// <param name="comparable">
    /// Whether any signal was produced at all. False means the records shared no field both
    /// populated, so <paramref name="score"/> carries no information.
    /// </param>
    public static MatchDecision Classify(double score, bool comparable, MatchThresholds thresholds)
    {
        if (!comparable)
            return MatchDecision.NonComparable;

        // Both comparisons are inclusive, matching what all six implementations already did.
        if (score >= thresholds.AutoMatch)
            return MatchDecision.AutoMatch;

        return score >= thresholds.Review ? MatchDecision.Review : MatchDecision.NoMatch;
    }
}
