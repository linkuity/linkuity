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

    // ── Defect regression: two unrelated people sharing nothing but a birth date ──────────
    // On the FEBRL corpus the unconditional identifier floor produced 313 false auto-merges,
    // 100 % of them bare date-of-birth collisions: distinct people fused because they share a
    // birth date while name, contact details and address all disagree. Modelled on the real
    // pair rec-1069-dup-0 | rec-774-org (see .superpowers/phase0/baseline.md, Task C2).
    private static readonly Linkuity.Matching.Profiles.MatchingProfile IdentifierProfile =
        Linkuity.Matching.Profiles.DefaultMatchingProfileProvider.CreatePersonProfile()
            .WithCandidateRetrievalStrategy("blocking-linear");

    private static EntityRecord StoredWithIdentifierProfile(string id, IReadOnlyDictionary<string, string> fields)
        => TestRecords.Person(id, fields, KeyEngine.GenerateBlockingKeys(
            TestRecords.Person(id, fields, []), IdentifierProfile));

    [Fact]
    public void SharedDateOfBirthAlone_DoesNotMergeDistinctPeople()
    {
        var corpus = new[]
        {
            StoredWithIdentifierProfile("connor", new Dictionary<string, string>
            {
                ["first_name"] = "Connor",
                ["last_name"] = "Moerlakd",
                ["date_of_birth"] = "1935-12-22",
                ["email"] = "connor.moerlakd@example.com",
                ["phone"] = "0299991111",
                ["address_line"] = "Arthur Circle",
                ["postal_code"] = "2680"
            })
        };
        var incoming = StoredWithIdentifierProfile("jade", new Dictionary<string, string>
        {
            ["first_name"] = "Jade",
            ["last_name"] = "Zimmermann",
            ["date_of_birth"] = "1935-12-22",
            ["email"] = "jade.zimmermann@example.org",
            ["phone"] = "0733332222",
            ["address_line"] = "Nepean Place",
            ["postal_code"] = "4573"
        });

        var result = Engine.Resolve(incoming, corpus, IdentifierProfile);

        Assert.NotEqual(MatchDecision.AutoMatch, result.Decision);
        Assert.True(result.FinalScore < IdentifierProfile.AutoMatchThreshold,
            $"a bare date-of-birth collision must not auto-merge, got {result.FinalScore}");
    }
}
