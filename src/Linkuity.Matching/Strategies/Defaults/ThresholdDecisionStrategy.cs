using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Reproduces the durable matcher's three-band classification. The multi-cluster
/// guard (auto -> review when candidates span clusters) depends on durable cluster
/// state and stays in FileMetadataStore for this milestone; it is layered onto the
/// engine in Milestone 16.
/// </summary>
public sealed class ThresholdDecisionStrategy : IDecisionStrategy
{
    public string Name => "threshold";

    /// <summary>
    /// Delegates to the shared classifier. <c>comparable: true</c> is not an assumption that the
    /// records had comparable fields — it is that this signature cannot tell. It receives only a
    /// score, by which point the distinction between "compared and weak" and "nothing to compare"
    /// has already been lost. Reporting NonComparable here would be guessing, so the caller that
    /// still holds the signals is the one that reports it.
    /// </summary>
    public MatchDecision Decide(double topScore, MatchingProfile profile, ScoreScale scale)
        => MatchBandClassifier.Classify(topScore, comparable: true, profile.ThresholdsOn(scale));
}
