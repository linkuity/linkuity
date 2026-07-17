using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

public class WeightedScoringTests
{
    private static readonly ISimilarityEvaluator[] AllEvaluators =
    [
        new ExactSimilarityEvaluator(),
        new FuzzyTextSimilarityEvaluator(),
        new JaccardSimilarityEvaluator(),
        new NGramSimilarityEvaluator(),
        new NumericSimilarityEvaluator(),
        new DateSimilarityEvaluator()
    ];

    private static MatchingProfile ProfileWith(params ProfileField[] fields) => new()
    {
        ContentType = "test",
        Fields = fields,
        NormalizationStrategy = "semantic-field",
        BlockingStrategies = ["exact-value"],
        CandidateRetrievalStrategy = "linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = "weighted",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 0.90,
        ReviewThreshold = 0.75
    };

    private static ProfileField Pf(string name, string evaluator, double weight)
        => new() { Name = name, SemanticType = SemanticFieldType.FullName, Roles = FieldRole.Matchable, SimilarityEvaluator = evaluator, Weight = weight };

    private static EntityRecord Rec(IReadOnlyDictionary<string, string> fields)
        => TestRecords.Person("r", fields);

    [Fact]
    public void FieldWeighted_EmitsOneSignalPerComparableMatchableField()
    {
        var profile = ProfileWith(Pf("first_name", "fuzzy", 1.0), Pf("email", "exact", 3.0));
        var strategy = new WeightedFieldSimilarityStrategy(AllEvaluators);

        var signals = strategy.Evaluate(
            Rec(new Dictionary<string, string> { ["first_name"] = "Jon", ["email"] = "a@x.com" }),
            Rec(new Dictionary<string, string> { ["first_name"] = "Jon", ["email"] = "a@x.com" }),
            profile);

        Assert.Equal(2, signals.Count);
        Assert.Contains(signals, s => s.Name == "first_name");
        Assert.Contains(signals, s => s.Name == "email" && s.Value == 1.0);
    }

    [Fact]
    public void FieldWeighted_SkipsFieldsMissingOnEitherSide()
    {
        var profile = ProfileWith(Pf("first_name", "fuzzy", 1.0), Pf("phone", "exact", 3.0));
        var strategy = new WeightedFieldSimilarityStrategy(AllEvaluators);

        var signals = strategy.Evaluate(
            Rec(new Dictionary<string, string> { ["first_name"] = "Jon", ["phone"] = "555" }),
            Rec(new Dictionary<string, string> { ["first_name"] = "Jon" }),
            profile);

        Assert.Single(signals);
        Assert.Equal("first_name", signals[0].Name);
    }

    [Fact]
    public void FieldWeighted_DefaultsToExactWhenEvaluatorUnset()
    {
        var profile = ProfileWith(new ProfileField { Name = "email", SemanticType = SemanticFieldType.Email, Roles = FieldRole.Matchable });
        var strategy = new WeightedFieldSimilarityStrategy(AllEvaluators);

        var signals = strategy.Evaluate(
            Rec(new Dictionary<string, string> { ["email"] = "A@x.com" }),
            Rec(new Dictionary<string, string> { ["email"] = "a@x.com" }),
            profile);

        Assert.Equal(1.0, Assert.Single(signals).Value);
    }

    [Fact]
    public void FieldWeighted_ThrowsForUnregisteredEvaluator()
    {
        var profile = ProfileWith(Pf("first_name", "no-such-evaluator", 1.0));
        var strategy = new WeightedFieldSimilarityStrategy(AllEvaluators);

        Assert.Throws<KeyNotFoundException>(() => strategy.Evaluate(
            Rec(new Dictionary<string, string> { ["first_name"] = "Jon" }),
            Rec(new Dictionary<string, string> { ["first_name"] = "Jon" }),
            profile));
    }

    [Fact]
    public void Weighted_FinalScoreIsWeightNormalizedAverage()
    {
        var profile = ProfileWith(Pf("first_name", "fuzzy", 1.0), Pf("email", "exact", 3.0));
        var scoring = new WeightedScoringStrategy();

        // first_name 0.5 (weight 1), email 1.0 (weight 3) -> (0.5 + 3.0) / 4 = 0.875
        var result = scoring.Score(
            [new SimilaritySignal("first_name", 0.5), new SimilaritySignal("email", 1.0)],
            profile);

        Assert.Equal(0.875, result.FinalScore, 10);
    }

    [Fact]
    public void Weighted_BreakdownContributionsSumToFinalScore()
    {
        var profile = ProfileWith(Pf("first_name", "fuzzy", 1.0), Pf("email", "exact", 3.0));
        var scoring = new WeightedScoringStrategy();

        var result = scoring.Score(
            [new SimilaritySignal("first_name", 0.5), new SimilaritySignal("email", 1.0)],
            profile);

        Assert.Equal(2, result.Breakdown.Count);
        Assert.Equal(result.FinalScore, result.Breakdown.Sum(c => c.Contribution), 10);
        var email = Assert.Single(result.Breakdown, c => c.Signal == "email");
        Assert.Equal(1.0, email.Value);
        Assert.Equal(3.0, email.Weight);
        Assert.Equal(0.75, email.Contribution, 10);
    }

    [Fact]
    public void Weighted_NoSignalsYieldsZeroAndEmptyBreakdown()
    {
        var profile = ProfileWith(Pf("email", "exact", 3.0));
        var scoring = new WeightedScoringStrategy();

        var result = scoring.Score([], profile);

        Assert.Equal(0, result.FinalScore);
        Assert.Empty(result.Breakdown);
    }

    [Fact]
    public void Headline_FuzzyTypoContributesSimilarityTheOldTokenScoreMissed()
    {
        // Old token similarity: "Jonathon" and "Johnathon" share no whole tokens -> 0.
        Assert.Equal(0.0, MatchKey.TokenSimilarity(
            new Dictionary<string, string> { ["first_name"] = "Jonathon" },
            new Dictionary<string, string> { ["first_name"] = "Johnathon" }), 10);

        // New fuzzy evaluator: a one-character typo scores high.
        var fuzzy = new FuzzyTextSimilarityEvaluator();
        var field = Pf("first_name", "fuzzy", 1.0);
        Assert.True(fuzzy.Evaluate("Jonathon", "Johnathon", field) > 0.8);
    }

    [Fact]
    public void Headline_TransposedTokensContributeSimilarityTheOldExactScoreMissed()
    {
        // Old exact key: "John Smith" and "Smith John" normalize to different keys -> 0.
        var exact = new ExactSimilarityEvaluator();
        Assert.Equal(0.0, exact.Evaluate("John Smith", "Smith John", Pf("full_name", "exact", 1.0)));

        // New fuzzy token-set: transposed tokens score full credit.
        var fuzzy = new FuzzyTextSimilarityEvaluator();
        Assert.True(fuzzy.Evaluate("John Smith", "Smith John", Pf("full_name", "fuzzy", 1.0)) >= 0.99);
    }
}
