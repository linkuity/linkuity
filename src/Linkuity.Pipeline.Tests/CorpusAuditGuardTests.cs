using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;

namespace Linkuity.Pipeline.Tests;

public class CorpusAuditGuardTests
{
    private static CorpusAuditService NewService() => new(MatchingDefaults.CreateRegistry());

    private static readonly EntityRecord[] Records =
        [CorpusAuditFixtures.Org("a", "ACME WIDGETS INC"), CorpusAuditFixtures.Org("b", "ACME WIDGETS")];

    private static readonly Dictionary<string, string> Truth = new() { ["a"] = "acme", ["b"] = "acme" };

    [Theory]
    [InlineData("normalizationStrategy")]
    [InlineData("similarityStrategy")]
    [InlineData("scoringStrategy")]
    [InlineData("decisionStrategy")]
    [InlineData("clusteringStrategy")]
    public void RejectsProfileOutsideFidelityEnvelope_NamingTheSetting(string setting)
    {
        var b = CorpusAuditFixtures.Profile();
        var profile = setting switch
        {
            "normalizationStrategy" => CorpusAuditFixtures.Clone(b, normalizationStrategy: "lowercase"),
            "similarityStrategy"    => CorpusAuditFixtures.Clone(b, similarityStrategy: "not-a-strategy"),
            "scoringStrategy"       => CorpusAuditFixtures.Clone(b, scoringStrategy: "not-a-strategy"),
            "decisionStrategy"      => CorpusAuditFixtures.Clone(b, decisionStrategy: "not-a-strategy"),
            _                       => CorpusAuditFixtures.Clone(b, clusteringStrategy: "hierarchical")
        };

        var ex = Assert.Throws<ArgumentException>(() => NewService().Audit(Records, profile, Truth));
        Assert.Contains(setting, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsDuplicateSourceRecordId()
    {
        EntityRecord[] dupes = [CorpusAuditFixtures.Org("a", "ACME INC"), CorpusAuditFixtures.Org("a", "ACME")];
        var ex = Assert.Throws<ArgumentException>(
            () => NewService().Audit(dupes, CorpusAuditFixtures.Profile(), Truth));
        Assert.Contains("'a'", ex.Message, StringComparison.Ordinal);
    }
}
