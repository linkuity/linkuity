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

    /// <summary>
    /// The Profile() shape plus a legal_form-like field declaring "8888" as a sentinel — the ELF
    /// code for "legal form not provided" that sits on 10% of the GLEIF corpus. Values compared
    /// case- and trim-insensitively against the declared list per <see cref="ProfileField.IsAbsent"/>.
    /// </summary>
    private static MatchingProfile ProfileWithSentinel(params string[] sentinels)
    {
        var profile = Profile();
        return profile with
        {
            Fields = profile.Fields.Append(new ProfileField
            {
                Name = "legal_form",
                SemanticType = SemanticFieldType.LegalForm,
                Roles = FieldRole.Matchable,
                SimilarityEvaluator = "exact",
                Weight = 1.0,
                NullEquivalents = sentinels
            }).ToList()
        };
    }

    private static IReadOnlyList<SimilaritySignal> EvaluateWithSentinel(
        EntityRecord left, EntityRecord right, params string[] sentinels)
        => new WeightedFieldSimilarityStrategy(MatchingDefaults.CreateRegistry().Evaluators.Values)
            .Evaluate(left, right, ProfileWithSentinel(sentinels));

    [Fact]
    public void DeclaredSentinel_OnBothSides_IsMissingBoth_NotCompared()
    {
        // The motivating defect: legal_form = "8888" ("not provided") must never read as two
        // companies agreeing on having no legal form.
        var signals = EvaluateWithSentinel(
            Record(("organization_name", "ACME"), ("legal_form", "8888")),
            Record(("organization_name", "ACME"), ("legal_form", "8888")),
            "8888");

        Assert.Equal(ComparisonOutcome.MissingBoth, signals.Single(s => s.Name == "legal_form").Outcome);
    }

    [Fact]
    public void DeclaredSentinel_OnOneSideOnly_IsMissingOneSide()
    {
        var signals = EvaluateWithSentinel(
            Record(("organization_name", "ACME"), ("legal_form", "8888")),
            Record(("organization_name", "ACME"), ("legal_form", "LLC")),
            "8888");

        Assert.Equal(ComparisonOutcome.MissingOneSide, signals.Single(s => s.Name == "legal_form").Outcome);
    }

    [Fact]
    public void DeclaredSentinel_ComparisonIsCaseAndTrimInsensitive()
    {
        var signals = EvaluateWithSentinel(
            Record(("organization_name", "ACME"), ("legal_form", " 8888 ")),
            Record(("organization_name", "ACME"), ("legal_form", "8888")),
            "8888");

        Assert.Equal(ComparisonOutcome.MissingBoth, signals.Single(s => s.Name == "legal_form").Outcome);
    }

    [Fact]
    public void UndeclaredValue_IsNotTreatedAsASentinel_EvenIfItLooksLikeOne()
    {
        // Only values the profile actually names are sentinels; nothing in the engine hard-codes
        // "8888" or any other literal.
        var signals = EvaluateWithSentinel(
            Record(("organization_name", "ACME"), ("legal_form", "8888")),
            Record(("organization_name", "ACME"), ("legal_form", "8888")),
            "9999"); // declared sentinel does not match the value actually present

        Assert.Equal(ComparisonOutcome.Compared, signals.Single(s => s.Name == "legal_form").Outcome);
    }

    [Fact]
    public void NoNullEquivalentsDeclared_SentinelLikeValueComparesNormally()
    {
        // Absent nullEquivalents changes nothing: "8888" is just a value like any other unless
        // the profile says otherwise.
        var signals = Evaluate(
            Record(("organization_name", "ACME"), ("postal_code", "8888")),
            Record(("organization_name", "ACME"), ("postal_code", "8888")));

        Assert.Equal(ComparisonOutcome.Compared, signals.Single(s => s.Name == "postal_code").Outcome);
    }

    [Fact]
    public void DeclaredSentinelAgreement_ContributesZeroEvidence_EndToEnd()
    {
        // The full acceptance criterion: two records whose only agreement is a declared sentinel
        // produce zero evidence, not agreement, from similarity through to the evidence scorer.
        var withSentinel = ProfileWithSentinel("8888");
        var profile = withSentinel with
        {
            Fields = withSentinel.Fields
                .Select(f => f.Name == "legal_form"
                    ? f with { Evidence = new FieldEvidence { SameEntityAgreement = 0.9, ChanceAgreement = 0.01 } }
                    : f)
                .ToList()
        };

        var signals = new WeightedFieldSimilarityStrategy(MatchingDefaults.CreateRegistry().Evaluators.Values)
            .Evaluate(
                Record(("organization_name", "ACME"), ("legal_form", "8888")),
                Record(("organization_name", "ACME"), ("legal_form", "8888")),
                profile);

        var legalFormSignal = signals.Single(s => s.Name == "legal_form");
        var result = new EvidenceScoringStrategy().Score([legalFormSignal], profile);

        Assert.Equal(ComparisonOutcome.MissingBoth, legalFormSignal.Outcome);
        Assert.Equal(0.0, result.FinalScore, 12);
    }

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
