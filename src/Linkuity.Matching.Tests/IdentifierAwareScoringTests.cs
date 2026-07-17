using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

public class IdentifierAwareScoringTests
{
    private static readonly MatchingProfile Profile = DefaultMatchingProfileProvider.CreatePersonProfile();
    private static readonly IScoringStrategy Scorer = new IdentifierAwareWeightedScoringStrategy();

    [Fact]
    public void Name_IsIdentifierWeighted()
        => Assert.Equal("identifier-weighted", Scorer.Name);

    [Fact]
    public void ExactEmailMatch_FloorsToAutoBand_EvenWhenOtherFieldsConflict()
    {
        // email (identifier) matches 1.0; first_name conflicts 0.0 -> still auto band.
        var signals = new List<SimilaritySignal>
        {
            new("email", 1.0),
            new("first_name", 0.0)
        };
        var result = Scorer.Score(signals, Profile);
        Assert.True(result.FinalScore >= 0.90, $"expected auto band, got {result.FinalScore}");
    }

    [Fact]
    public void ExactPhoneMatch_FloorsToAutoBand()
    {
        var signals = new List<SimilaritySignal> { new("phone", 1.0), new("email", 0.0) };
        var result = Scorer.Score(signals, Profile);
        Assert.True(result.FinalScore >= 0.90, $"expected auto band, got {result.FinalScore}");
    }

    [Fact]
    public void WeakSharedNameWithConflict_BelowGate_IsNotFlooredToReview()
    {
        // last_name matches (w2) but first_name barely (0.2, w1) and email conflicts (0.0, w3):
        // weighted = (2*1 + 1*0.2 + 3*0)/6 = 0.367 < gate 0.75 -> the review floor must NOT apply.
        // Under the old unconditional floor this returned 0.80 and flooded the review queue.
        var signals = new List<SimilaritySignal>
        {
            new("last_name", 1.0),
            new("first_name", 0.2),
            new("email", 0.0)
        };
        var result = Scorer.Score(signals, Profile);
        Assert.True(result.FinalScore < 0.75,
            $"expected raw weighted score below the review threshold, got {result.FinalScore}");
    }

    [Fact]
    public void SharedSurnameDifferentIdentifiers_BelowGate_ProducesNoReview()
    {
        // The reported pathology (docs/performance-testing-plan.md): Daniel Lopez vs Robert Lopez,
        // sharing only the surname, differing email + phone. name fuzzy ~0.59 (w1.5), email 0 (w3),
        // phone 0 (w3): weighted = (1.5*0.59)/7.5 = 0.118 < gate -> not floored, no review task.
        var signals = new List<SimilaritySignal>
        {
            new("name", 0.59),
            new("email", 0.0),
            new("phone", 0.0)
        };
        var result = Scorer.Score(signals, Profile);
        Assert.True(result.FinalScore < 0.75,
            $"a surname-only coincidence must not reach the review band, got {result.FinalScore}");
    }

    [Fact]
    public void WeightedAtGate_IsFlooredToReviewBand()
    {
        // email (Matchable, NOT Identifier) matches 1.0 (w3) + first_name conflicts 0.0 (w1):
        // weighted = 3/4 = 0.75 == gate -> the review floor applies and lifts it to 0.80.
        var profile = TwoFieldProfile(FieldRole.Matchable); // no Identifier role
        var result = Scorer.Score([new("email", 1.0), new("first_name", 0.0)], profile);
        Assert.True(result.FinalScore >= 0.80 && result.FinalScore < 0.90,
            $"expected the review floor at the gate boundary, got {result.FinalScore}");
    }

    [Fact]
    public void StrongNonIdentifierSimilarity_CanExceedReviewFloor()
    {
        // identical names, no identifier -> weighted avg ~1.0 exceeds the 0.80 floor.
        var signals = new List<SimilaritySignal>
        {
            new("first_name", 1.0),
            new("last_name", 1.0)
        };
        var result = Scorer.Score(signals, Profile);
        Assert.True(result.FinalScore > 0.90, $"expected high score, got {result.FinalScore}");
    }

    [Fact]
    public void EmptySignals_ScoreZero()
        => Assert.Equal(0, Scorer.Score([], Profile).FinalScore);

    [Fact]
    public void Breakdown_CoversEverySignal()
    {
        var signals = new List<SimilaritySignal> { new("email", 1.0), new("last_name", 0.5) };
        var result = Scorer.Score(signals, Profile);
        Assert.Equal(2, result.Breakdown.Count);
    }

    private static MatchingProfile TwoFieldProfile(FieldRole emailRoles, double reviewFloorGate = 0.75) => new()
    {
        ContentType = "t",
        Fields =
        [
            new ProfileField { Name = "email", SemanticType = SemanticFieldType.Email, Roles = emailRoles, SimilarityEvaluator = "exact", Weight = 3.0 },
            new ProfileField { Name = "first_name", SemanticType = SemanticFieldType.FirstName, Roles = FieldRole.Matchable, SimilarityEvaluator = "fuzzy", Weight = 1.0 }
        ],
        NormalizationStrategy = "identity",
        BlockingStrategies = ["exact-value"],
        CandidateRetrievalStrategy = "linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = "identifier-weighted",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 0.90,
        ReviewThreshold = 0.75,
        ReviewFloorGate = reviewFloorGate
    };

    [Fact]
    public void EmailWithoutIdentifierRole_DoesNotFloorToAuto()
    {
        // email matches 1.0 but first_name conflicts 0.0; without the Identifier role the
        // weighted average (3*1 + 1*0)/4 = 0.75 governs -> review band, NOT auto.
        var profile = TwoFieldProfile(FieldRole.Matchable); // no Identifier
        var result = Scorer.Score([new("email", 1.0), new("first_name", 0.0)], profile);
        Assert.True(result.FinalScore < 0.90, $"expected review band, got {result.FinalScore}");
    }

    [Fact]
    public void EmailWithIdentifierRole_FloorsToAuto()
    {
        var profile = TwoFieldProfile(FieldRole.Matchable | FieldRole.Identifier);
        var result = Scorer.Score([new("email", 1.0), new("first_name", 0.0)], profile);
        Assert.True(result.FinalScore >= 0.90, $"expected auto band, got {result.FinalScore}");
    }

    [Fact]
    public void ConfiguredLowerGate_PromotesSubDefaultGatePairToReviewFloor()
    {
        // weighted = (3*0.6 + 1*0)/4 = 0.45. Below the default gate (0.75) this is a NoMatch, but a
        // profile that lowers the gate to 0.30 promotes it to the 0.80 review floor.
        var profile = TwoFieldProfile(FieldRole.Matchable, reviewFloorGate: 0.30);
        var result = Scorer.Score([new("email", 0.6), new("first_name", 0.0)], profile);
        Assert.True(result.FinalScore >= 0.80 && result.FinalScore < 0.90,
            $"expected the review floor with a lowered gate, got {result.FinalScore}");
    }

    [Fact]
    public void DefaultGate_LeavesSameSubGatePairBelowReview()
    {
        // Same signals (weighted 0.45), default gate 0.75 -> not floored -> raw weighted score.
        var profile = TwoFieldProfile(FieldRole.Matchable); // reviewFloorGate defaults to 0.75
        var result = Scorer.Score([new("email", 0.6), new("first_name", 0.0)], profile);
        Assert.True(result.FinalScore < 0.75,
            $"expected the raw weighted score below review at the default gate, got {result.FinalScore}");
    }
}
