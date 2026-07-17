using Linkuity.Core.Models;
using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

public class SimilarityScoringTests
{
    private static readonly ISimilarityStrategy Similarity = new DefaultSimilarityStrategy();
    private static readonly IScoringStrategy Scoring = new DefaultScoringStrategy();

    private static double ScorePair(EntityRecord left, EntityRecord right)
        => Scoring.Score(Similarity.Evaluate(left, right, TestProfiles.Person), TestProfiles.Person).FinalScore;

    [Fact]
    public void Score_IsZeroWhenNoSharedBlockingKey()
    {
        var left = TestRecords.Person("a", new Dictionary<string, string> { ["email"] = "a@x.com" }, ["email:axcom"]);
        var right = TestRecords.Person("b", new Dictionary<string, string> { ["email"] = "b@y.com" }, ["email:bycom"]);
        Assert.Equal(0, ScorePair(left, right));
    }

    [Fact]
    public void Score_Is098OnSharedExactIdentifier()
    {
        var left = TestRecords.Person("a", new Dictionary<string, string> { ["email"] = "alice@example.com", ["name"] = "Alice" }, ["email:aliceexamplecom"]);
        var right = TestRecords.Person("b", new Dictionary<string, string> { ["email"] = "alice@example.com", ["name"] = "Alice Verified" }, ["email:aliceexamplecom"]);
        Assert.Equal(0.98, ScorePair(left, right));
    }

    [Fact]
    public void Score_FloorsAt080OnSharedNameTokenWithoutExactIdentifier()
    {
        // shared "name:smith" blocking key, different emails, partial token overlap
        var left = TestRecords.Person("a",
            new Dictionary<string, string> { ["last_name"] = "Smith", ["email"] = "a@x.com", ["first_name"] = "Alice" },
            ["name:smith", "email:axcom"]);
        var right = TestRecords.Person("b",
            new Dictionary<string, string> { ["last_name"] = "Smith", ["email"] = "b@y.com", ["first_name"] = "Bob" },
            ["name:smith", "email:bycom"]);
        Assert.Equal(0.80, ScorePair(left, right));
    }

    [Fact]
    public void Breakdown_NamesTheContributingSignal()
    {
        var left = TestRecords.Person("a", new Dictionary<string, string> { ["phone"] = "+15551234567" }, ["phone:15551234567"]);
        var right = TestRecords.Person("b", new Dictionary<string, string> { ["phone"] = "+15551234567" }, ["phone:15551234567"]);
        var result = Scoring.Score(Similarity.Evaluate(left, right, TestProfiles.Person), TestProfiles.Person);
        Assert.Equal(0.98, result.FinalScore);
        Assert.Contains(result.Breakdown, c => c.Signal == "exact:phone" && c.Contribution == 0.98);
    }
}
