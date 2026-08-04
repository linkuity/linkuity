using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

public class EvidenceScoringAliasTests
{
    private static ProfileField Field(string name, string? aliasGroup) => new()
    {
        Name = name,
        SemanticType = SemanticFieldType.FullName,
        Roles = FieldRole.Matchable,
        SimilarityEvaluator = "exact",
        AliasGroup = aliasGroup,
        Evidence = new FieldEvidence { SameEntityAgreement = 0.9, ChanceAgreement = 0.01, MaxAgreementBits = 6.0 }
    };

    private static MatchingProfile Profile(params ProfileField[] fields) => new()
    {
        ContentType = "person",
        Fields = fields,
        NormalizationStrategy = "identity",
        BlockingStrategies = ["token"],
        CandidateRetrievalStrategy = "linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = "evidence",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 8.0,
        ReviewThreshold = 4.0
    };

    [Fact]
    public void TwoSpellingsOfTheSameFact_AreCountedOnce()
    {
        var profile = Profile(Field("full_name", "name"), Field("name", "name"));

        var result = new EvidenceScoringStrategy().Score(
            [new SimilaritySignal("full_name", 1.0), new SimilaritySignal("name", 1.0)], profile);

        Assert.Equal(6.0, result.FinalScore, 6);   // not 12.0
    }

    [Fact]
    public void TheStrongestMemberOfTheGroupIsTheOneThatCounts()
    {
        var profile = Profile(Field("full_name", "name"), Field("name", "name"));

        var result = new EvidenceScoringStrategy().Score(
            [new SimilaritySignal("full_name", 0.2), new SimilaritySignal("name", 1.0)], profile);

        Assert.Equal(6.0, result.FinalScore, 6);
    }

    [Fact]
    public void AGroupWhoseMembersAreAllMissing_ContributesZeroNotADisagreement()
    {
        var profile = Profile(Field("full_name", "name"), Field("name", "name"));

        var result = new EvidenceScoringStrategy().Score(
            [
                new SimilaritySignal("full_name", 0, ComparisonOutcome.MissingBoth),
                new SimilaritySignal("name", 0, ComparisonOutcome.MissingOneSide)
            ], profile);

        Assert.Equal(0.0, result.FinalScore, 12);
    }

    [Fact]
    public void FieldsWithNoAliasGroup_AreIndependentEvenWhenTheyShareATypeAndWeight()
    {
        var profile = Profile(Field("full_name", null), Field("name", null));

        var result = new EvidenceScoringStrategy().Score(
            [new SimilaritySignal("full_name", 1.0), new SimilaritySignal("name", 1.0)], profile);

        Assert.Equal(12.0, result.FinalScore, 6);
    }

    [Fact]
    public void TheBreakdownStillListsEveryFieldInTheGroup()
    {
        // The suppressed member must remain visible with a zero contribution: a field that
        // vanishes from the explanation looks like a field that was never evaluated.
        var profile = Profile(Field("full_name", "name"), Field("name", "name"));

        var result = new EvidenceScoringStrategy().Score(
            [new SimilaritySignal("full_name", 0.2), new SimilaritySignal("name", 1.0)], profile);

        // full_name is declared first, so it carries the group's winning contribution and the
        // later member carries zero. Deterministic because signal order is profile field order.
        Assert.Equal(2, result.Breakdown.Count);
        Assert.Equal(6.0, result.Breakdown.Single(b => b.Signal == "full_name").Contribution, 6);
        Assert.Equal(0.0, result.Breakdown.Single(b => b.Signal == "name").Contribution, 12);
    }
}
