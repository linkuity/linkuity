using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies;

public interface IDecisionStrategy
{
    string Name { get; }
    MatchDecision Decide(double topScore, MatchingProfile profile);
}
