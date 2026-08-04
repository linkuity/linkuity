using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

public class ComparisonOutcomeTests
{
    private static MatchingProfile Profile() => new()
    {
        ContentType = "organization",
        Fields =
        [
            new ProfileField
            {
                Name = "organization_name",
                SemanticType = SemanticFieldType.OrganizationName,
                Roles = FieldRole.Matchable,
                SimilarityEvaluator = "exact",
                Weight = 2.0
            },
            new ProfileField
            {
                Name = "postal_code",
                SemanticType = SemanticFieldType.PostalCode,
                Roles = FieldRole.Matchable,
                SimilarityEvaluator = "exact",
                Weight = 1.0
            }
        ],
        NormalizationStrategy = "identity",
        BlockingStrategies = ["token"],
        CandidateRetrievalStrategy = "linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = "identifier-weighted",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 0.9,
        ReviewThreshold = 0.75
    };

    private static EntityRecord Record(params (string Field, string Value)[] fields) => new()
    {
        Id = Guid.NewGuid(), ProjectId = Guid.Empty, SourceId = Guid.Empty, IngestBatchId = Guid.Empty,
        SourceRecordId = Guid.NewGuid().ToString(),
        Fields = fields.ToDictionary(f => f.Field, f => f.Value, StringComparer.Ordinal),
        CreatedAt = DateTimeOffset.UnixEpoch
    };

    private static IReadOnlyList<SimilaritySignal> Evaluate(EntityRecord left, EntityRecord right)
        => new WeightedFieldSimilarityStrategy(MatchingDefaults.CreateRegistry().Evaluators.Values)
            .Evaluate(left, right, Profile());

    [Fact]
    public void EveryMatchableField_EmitsASignal_EvenWhenNeitherRecordHasIt()
    {
        var signals = Evaluate(
            Record(("organization_name", "ACME")),
            Record(("organization_name", "ACME")));

        Assert.Equal(2, signals.Count);
        Assert.Equal(ComparisonOutcome.Compared, signals.Single(s => s.Name == "organization_name").Outcome);
        Assert.Equal(ComparisonOutcome.MissingBoth, signals.Single(s => s.Name == "postal_code").Outcome);
    }

    [Fact]
    public void OneSidedAbsence_IsDistinguishableFromBothSidesAbsent()
    {
        var signals = Evaluate(
            Record(("organization_name", "ACME"), ("postal_code", "62701")),
            Record(("organization_name", "ACME")));

        Assert.Equal(ComparisonOutcome.MissingOneSide, signals.Single(s => s.Name == "postal_code").Outcome);
    }

    [Fact]
    public void BlankIsTreatedAsAbsent_NotAsAValueThatDisagrees()
    {
        var signals = Evaluate(
            Record(("organization_name", "ACME"), ("postal_code", "   ")),
            Record(("organization_name", "ACME"), ("postal_code", "62701")));

        Assert.Equal(ComparisonOutcome.MissingOneSide, signals.Single(s => s.Name == "postal_code").Outcome);
    }

    [Fact]
    public void ValueIsZeroOnEveryOutcomeExceptCompared()
    {
        var signals = Evaluate(
            Record(("organization_name", "ACME")),
            Record(("organization_name", "ACME")));

        Assert.All(signals.Where(s => s.Outcome != ComparisonOutcome.Compared),
            s => Assert.Equal(0.0, s.Value));
    }

    [Fact]
    public void ExistingScorersIgnoreNonComparedSignals_SoTheScoreIsUnchanged()
    {
        // The whole migration guarantee in one assertion: a profile field neither record
        // populates must not move the score, whether or not a signal is emitted for it.
        var profile = Profile();
        var scorer = new IdentifierAwareWeightedScoringStrategy();

        var withMissing = scorer.Score(
            [
                new SimilaritySignal("organization_name", 1.0),
                new SimilaritySignal("postal_code", 0.0, ComparisonOutcome.MissingBoth)
            ], profile);

        var withoutMissing = scorer.Score([new SimilaritySignal("organization_name", 1.0)], profile);

        Assert.Equal(withoutMissing.FinalScore, withMissing.FinalScore, 12);
    }
}
