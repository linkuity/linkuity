using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies;

public interface IDecisionStrategy
{
    string Name { get; }

    /// <summary>
    /// <paramref name="scale"/> is the scale <paramref name="topScore"/> is actually expressed on
    /// — the resolved scoring strategy's own <see cref="ScoreScale"/>, supplied by the caller
    /// (<c>MatchingEngine.Resolve</c> resolves it two lines above this call) rather than assumed.
    /// A strategy that builds <see cref="MatchThresholds"/> from <c>profile</c>'s raw
    /// thresholds must validate them against this scale, not against a default.
    /// </summary>
    MatchDecision Decide(double topScore, MatchingProfile profile, ScoreScale scale);
}
