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

    public MatchDecision Decide(double topScore, MatchingProfile profile)
    {
        if (topScore >= profile.AutoMatchThreshold)
            return MatchDecision.AutoMatch;
        if (topScore >= profile.ReviewThreshold)
            return MatchDecision.Review;
        return MatchDecision.NoMatch;
    }
}
