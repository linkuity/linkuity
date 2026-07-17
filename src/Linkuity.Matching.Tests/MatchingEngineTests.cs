using Linkuity.Core.Models;
using Linkuity.Matching;

namespace Linkuity.Matching.Tests;

public class MatchingEngineTests
{
    private static readonly MatchingEngine Engine = MatchingDefaults.CreateEngine();
    private static readonly Linkuity.Matching.Profiles.MatchingProfile Profile = MatchingDefaults.CreateParityPersonProfile();
    private static readonly MatchingEngine KeyEngine = MatchingDefaults.CreateEngine();

    private static EntityRecord Stored(string id, IReadOnlyDictionary<string, string> fields)
    {
        var seed = TestRecords.Person(id, fields, []);
        return TestRecords.Person(id, fields, KeyEngine.GenerateBlockingKeys(seed, Profile));
    }

    [Fact]
    public void Resolve_AutoMatchesOnSharedEmail()
    {
        var corpus = new[] { Stored("a", new Dictionary<string, string> { ["email"] = "alice@example.com", ["name"] = "Alice" }) };
        var incoming = Stored("b", new Dictionary<string, string> { ["email"] = "alice@example.com", ["name"] = "Alice Verified" });

        var result = Engine.Resolve(incoming, corpus, Profile);

        Assert.Equal(MatchDecision.AutoMatch, result.Decision);
        Assert.Equal(0.98, result.FinalScore);
        Assert.Single(result.Candidates);
        Assert.NotEmpty(result.Breakdown);
    }

    [Fact]
    public void Resolve_ReviewsOnSharedNameTokenOnly()
    {
        var corpus = new[] { Stored("a", new Dictionary<string, string> { ["last_name"] = "Smith", ["email"] = "a@x.com", ["first_name"] = "Alice" }) };
        var incoming = Stored("b", new Dictionary<string, string> { ["last_name"] = "Smith", ["email"] = "b@y.com", ["first_name"] = "Bob" });

        var result = Engine.Resolve(incoming, corpus, Profile);

        Assert.Equal(MatchDecision.Review, result.Decision);
        Assert.Equal(0.80, result.FinalScore);
    }

    [Fact]
    public void Resolve_NoMatchWhenNoSharedBlockingKey()
    {
        var corpus = new[] { Stored("a", new Dictionary<string, string> { ["email"] = "a@x.com", ["last_name"] = "Jones" }) };
        var incoming = Stored("b", new Dictionary<string, string> { ["email"] = "b@y.com", ["last_name"] = "Smith" });

        var result = Engine.Resolve(incoming, corpus, Profile);

        Assert.Equal(MatchDecision.NoMatch, result.Decision);
        Assert.Equal(0, result.FinalScore);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Resolve_OrdersCandidatesByScoreDescendingAndFiltersBelowReview()
    {
        var corpus = new[]
        {
            Stored("exact", new Dictionary<string, string> { ["email"] = "alice@example.com", ["last_name"] = "Smith" }),
            Stored("nametoken", new Dictionary<string, string> { ["email"] = "other@example.com", ["last_name"] = "Smith" })
        };
        var incoming = Stored("in", new Dictionary<string, string> { ["email"] = "alice@example.com", ["last_name"] = "Smith" });

        var result = Engine.Resolve(incoming, corpus, Profile);

        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal("exact", result.Candidates[0].Record.SourceRecordId);
        Assert.Equal(0.98, result.Candidates[0].Score);
        Assert.True(result.Candidates[0].Score >= result.Candidates[1].Score);
    }
}
