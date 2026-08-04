using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

public class EvidenceScoringStrategyTests
{
    private static ProfileField Field(string name, double m, double u, double? cap, FieldRole roles = FieldRole.Matchable)
        => new()
        {
            Name = name,
            SemanticType = SemanticFieldType.OrganizationName,
            Roles = roles,
            SimilarityEvaluator = "exact",
            Evidence = new FieldEvidence { SameEntityAgreement = m, ChanceAgreement = u, MaxAgreementBits = cap }
        };

    private static MatchingProfile Profile(params ProfileField[] fields) => new()
    {
        ContentType = "organization",
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

    private static readonly EvidenceScoringStrategy Scorer = new();

    [Fact]
    public void ItDeclaresTheLogOddsScale_SoThresholdsAreValidatedAgainstIt()
    {
        Assert.Equal(ScoreScale.LogOdds, Scorer.Scale);
        Assert.Equal(SignalShape.PerField, Scorer.Consumes);
        Assert.Equal("evidence", Scorer.Name);
    }

    [Fact]
    public void EvidenceAdds_ItDoesNotAverage()
    {
        var profile = Profile(
            Field("name", 0.9, 0.01, cap: 6.0),
            Field("postcode", 0.8, 0.05, cap: 3.0));

        var result = Scorer.Score(
            [new SimilaritySignal("name", 1.0), new SimilaritySignal("postcode", 1.0)], profile);

        Assert.Equal(9.0, result.FinalScore, 6);   // 6.0 + 3.0, both capped
    }

    [Fact]
    public void AMissingFieldContributesNothing_AndDoesNotRescaleTheRest()
    {
        // The measured defect in one assertion: the same agreement must be worth the same
        // whether or not the profile declares fields this pair happens not to share.
        var oneField = Profile(Field("name", 0.9, 0.01, cap: 6.0));
        var twoFields = Profile(Field("name", 0.9, 0.01, cap: 6.0), Field("postcode", 0.8, 0.05, cap: 3.0));

        var narrow = Scorer.Score([new SimilaritySignal("name", 1.0)], oneField);
        var wide = Scorer.Score(
            [
                new SimilaritySignal("name", 1.0),
                new SimilaritySignal("postcode", 0, ComparisonOutcome.MissingBoth)
            ], twoFields);

        Assert.Equal(narrow.FinalScore, wide.FinalScore, 12);
    }

    [Theory]
    [InlineData(ComparisonOutcome.MissingOneSide)]
    [InlineData(ComparisonOutcome.MissingBoth)]
    [InlineData(ComparisonOutcome.NotComparable)]
    public void EveryNonComparedOutcomeContributesExactlyZero(ComparisonOutcome outcome)
    {
        var profile = Profile(Field("name", 0.9, 0.01, cap: 6.0));

        var result = Scorer.Score([new SimilaritySignal("name", 0, outcome)], profile);

        Assert.Equal(0.0, result.FinalScore, 12);
        Assert.Equal(0.0, Assert.Single(result.Breakdown).Contribution, 12);
    }

    [Fact]
    public void DisagreementSubtracts()
    {
        var profile = Profile(Field("name", 0.9, 0.01, cap: 6.0));

        var result = Scorer.Score([new SimilaritySignal("name", 0.0)], profile);

        Assert.True(result.FinalScore < 0);
    }

    [Fact]
    public void TheBreakdownCarriesTheOutcome_SoAnExplanationCanSayWhyAFieldGaveNothing()
    {
        var profile = Profile(Field("name", 0.9, 0.01, cap: 6.0));

        var result = Scorer.Score([new SimilaritySignal("name", 0, ComparisonOutcome.MissingOneSide)], profile);

        Assert.Equal(ComparisonOutcome.MissingOneSide, Assert.Single(result.Breakdown).Outcome);
    }

    [Fact]
    public void AMatchableFieldWithoutEvidence_ThrowsAndNamesTheField()
    {
        // Never infer m and u from Weight. A profile that has not been given numbers must not run:
        // this is what stops a taxonomy shipping on the old scale in form only.
        var profile = Profile(new ProfileField
        {
            Name = "name",
            SemanticType = SemanticFieldType.OrganizationName,
            Roles = FieldRole.Matchable,
            Weight = 4.0
        });

        var ex = Assert.Throws<InvalidOperationException>(
            () => Scorer.Score([new SimilaritySignal("name", 1.0)], profile));

        Assert.Contains("name", ex.Message, StringComparison.Ordinal);
        Assert.Contains("evidence", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ANonMatchableFieldWithoutEvidence_IsNotAnError()
    {
        var profile = Profile(
            Field("name", 0.9, 0.01, cap: 6.0),
            new ProfileField
            {
                Name = "display_only",
                SemanticType = SemanticFieldType.OrganizationName,
                Roles = FieldRole.Searchable
            });

        var result = Scorer.Score([new SimilaritySignal("name", 1.0)], profile);

        Assert.Equal(6.0, result.FinalScore, 6);
    }

    [Fact]
    public void ASignalNamingNoProfileField_IsIgnoredRatherThanScoredAtSomeDefault()
    {
        var profile = Profile(Field("name", 0.9, 0.01, cap: 6.0));

        var result = Scorer.Score(
            [new SimilaritySignal("name", 1.0), new SimilaritySignal("mystery", 1.0)], profile);

        Assert.Equal(6.0, result.FinalScore, 6);
        Assert.Single(result.Breakdown);
    }

    [Fact]
    public void NoSignalsAtAll_ScoresZeroEvidence()
    {
        var result = Scorer.Score([], Profile(Field("name", 0.9, 0.01, cap: 6.0)));

        Assert.Equal(0.0, result.FinalScore, 12);
        Assert.Empty(result.Breakdown);
    }
}
