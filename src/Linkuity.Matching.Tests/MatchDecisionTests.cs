using Linkuity.Matching;

namespace Linkuity.Matching.Tests;

public class MatchDecisionTests
{
    [Fact]
    public void Decision_BandsAreOrderedFromNoMatchToAutoMatch()
    {
        Assert.True(MatchDecision.NoMatch < MatchDecision.Review);
        Assert.True(MatchDecision.Review < MatchDecision.AutoMatch);
    }
}
