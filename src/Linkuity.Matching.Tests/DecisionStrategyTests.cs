using Linkuity.Matching;
using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

public class DecisionStrategyTests
{
    private static readonly IDecisionStrategy Strategy = new ThresholdDecisionStrategy();

    [Theory]
    [InlineData(0.98, MatchDecision.AutoMatch)]
    [InlineData(0.90, MatchDecision.AutoMatch)]   // boundary: >= auto
    [InlineData(0.89, MatchDecision.Review)]
    [InlineData(0.75, MatchDecision.Review)]      // boundary: >= review
    [InlineData(0.74, MatchDecision.NoMatch)]
    [InlineData(0.0, MatchDecision.NoMatch)]
    public void Decide_ClassifiesByThresholdBands(double score, MatchDecision expected)
        => Assert.Equal(expected, Strategy.Decide(score, TestProfiles.Person));
}
