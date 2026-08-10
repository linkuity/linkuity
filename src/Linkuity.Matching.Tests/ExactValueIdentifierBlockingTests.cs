using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

public class ExactValueIdentifierBlockingTests
{
    private static MatchingProfile ProductLikeProfile() => new()
    {
        ContentType = "product",
        Fields =
        [
            new ProfileField { Name = "sku", SemanticType = SemanticFieldType.Sku, Roles = FieldRole.Matchable | FieldRole.Blocking | FieldRole.Identifier, SimilarityEvaluator = "exact" },
            new ProfileField { Name = "product_name", SemanticType = SemanticFieldType.ProductName, Roles = FieldRole.Matchable | FieldRole.Blocking, SimilarityEvaluator = "fuzzy" }
        ],
        NormalizationStrategy = "identity",
        BlockingStrategies = ["exact-value"],
        CandidateRetrievalStrategy = "linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = "identifier-weighted",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 0.90,
        ReviewThreshold = 0.75
    };

    private static EntityRecord Rec(Dictionary<string, string> fields) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = Guid.Empty,
        SourceId = Guid.Empty,
        IngestBatchId = Guid.Empty,
        SourceRecordId = "r",
        Fields = fields,
        BlockingKeys = [],
        CreatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public void IdentifierRoledField_OfNewSemanticType_ProducesExactValueKey()
    {
        var keys = new ExactValueBlockingStrategy().GenerateKeys(
            Rec(new() { ["sku"] = "ALPHA-100", ["product_name"] = "Widget Alpha" }), ProductLikeProfile());
        Assert.Contains(keys, k => k.StartsWith("sku:"));
        // product_name is a name type without the Identifier role -> no exact-value key.
        Assert.DoesNotContain(keys, k => k.StartsWith("product_name:"));
    }

    [Fact]
    public void PersonExactValueKeys_AreUnchanged_ByTheAdditiveRule()
    {
        var person = DefaultMatchingProfileProvider.CreatePersonProfile();
        var keys = new ExactValueBlockingStrategy().GenerateKeys(
            Rec(new() { ["email"] = "a@b.com", ["phone"] = "+1-415-555-0100", ["last_name"] = "Jones" }), person);
        Assert.Contains(keys, k => k.StartsWith("email:"));
        Assert.Contains(keys, k => k.StartsWith("phone:"));
        Assert.DoesNotContain(keys, k => k.StartsWith("last_name:")); // name field, not exact-value
    }

    // ── PostalCode: reachability without the identifier floor (Task 4) ────────────────────
    // A postal code is a shared-address signal, not a uniqueness signal: a registration-agent
    // address (e.g. Wilmington, DE 19801) is shared by thousands of unrelated companies. Declaring
    // it PostalCode-typed and Blocking must let it produce an exact-value key for candidate
    // retrieval WITHOUT also flooring IdentifierAwareWeightedScoringStrategy to auto-merge (0.98) —
    // that floor is reserved for fields the profile separately marks FieldRole.Identifier.

    /// <summary>
    /// organization_name (weight 4.0) + postal_code (weight 1.0), mirroring the brief's worked
    /// example: a weak name similarity of 0.25 plus an exact postal-code match weights to
    /// (4*0.25 + 1*1.0)/5 = 0.40 — clearing the default IdentifierFloorGate (0.35) but, absent the
    /// Identifier role on postal_code, never testing it.
    /// </summary>
    private static MatchingProfile OrgProfile(FieldRole postalCodeRoles) => new()
    {
        ContentType = "organization",
        Fields =
        [
            new ProfileField { Name = "organization_name", SemanticType = SemanticFieldType.OrganizationName, Roles = FieldRole.Matchable, SimilarityEvaluator = "fuzzy", Weight = 4.0 },
            new ProfileField { Name = "postal_code", SemanticType = SemanticFieldType.PostalCode, Roles = postalCodeRoles, SimilarityEvaluator = "exact", Weight = 1.0 }
        ],
        NormalizationStrategy = "identity",
        BlockingStrategies = ["exact-value"],
        CandidateRetrievalStrategy = "blocking-linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = "identifier-weighted",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 0.90,
        ReviewThreshold = 0.75
    };

    [Fact]
    public void PostalCode_DeclaredBlockingWithoutIdentifier_ProducesExactValueKey()
    {
        var profile = OrgProfile(FieldRole.Matchable | FieldRole.Blocking); // no Identifier
        var keys = new ExactValueBlockingStrategy().GenerateKeys(
            Rec(new() { ["organization_name"] = "Acme Holdings LLC", ["postal_code"] = "19801" }), profile);

        Assert.Contains(keys, k => k.StartsWith("postal_code:"));
    }

    [Fact]
    public void PostalCode_WithoutIdentifierRole_DoesNotFloorToAuto_EvenWhenGateClears()
    {
        // Weighted 0.40 clears the identifier gate (0.35) but postal_code carries no Identifier
        // role, so identifierMatched is never set and neither floor applies: the raw weighted
        // score stands. This is the specific auto-merge Wilmington-DE-19801 fusion the task exists
        // to prevent.
        var profile = OrgProfile(FieldRole.Matchable | FieldRole.Blocking); // no Identifier
        var scorer = new IdentifierAwareWeightedScoringStrategy();
        var signals = new List<SimilaritySignal>
        {
            new("organization_name", 0.25),
            new("postal_code", 1.0)
        };

        var result = scorer.Score(signals, profile);

        Assert.Equal(0.40, result.FinalScore, 10);
        Assert.True(result.FinalScore < profile.ReviewThreshold,
            $"a shared registration-agent postcode must not even reach review, got {result.FinalScore}");
        Assert.True(result.FinalScore < profile.AutoMatchThreshold,
            $"expected no auto-merge floor, got {result.FinalScore}");
    }

    [Fact]
    public void PostalCode_DeclaredIdentifier_StillFloorsToAuto_UnaffectedByTheChange()
    {
        // Contrast case: when a profile explicitly marks postal_code Identifier (its own choice,
        // unchanged by this task), the same signals still floor to 0.98 exactly as before.
        var profile = OrgProfile(FieldRole.Matchable | FieldRole.Blocking | FieldRole.Identifier);
        var scorer = new IdentifierAwareWeightedScoringStrategy();
        var signals = new List<SimilaritySignal>
        {
            new("organization_name", 0.25),
            new("postal_code", 1.0)
        };

        var result = scorer.Score(signals, profile);

        Assert.Equal(0.98, result.FinalScore, 10);
    }

    [Fact]
    public void PostalCode_MatchingDeclaredNullEquivalent_ProducesNoKey()
    {
        var profile = OrgProfile(FieldRole.Matchable | FieldRole.Blocking) with
        {
            Fields =
            [
                new ProfileField { Name = "organization_name", SemanticType = SemanticFieldType.OrganizationName, Roles = FieldRole.Matchable, SimilarityEvaluator = "fuzzy", Weight = 4.0 },
                new ProfileField { Name = "postal_code", SemanticType = SemanticFieldType.PostalCode, Roles = FieldRole.Matchable | FieldRole.Blocking, SimilarityEvaluator = "exact", Weight = 1.0, NullEquivalents = ["00000"] }
            ]
        };

        var keys = new ExactValueBlockingStrategy().GenerateKeys(
            Rec(new() { ["organization_name"] = "Acme Holdings LLC", ["postal_code"] = "00000" }), profile);

        Assert.DoesNotContain(keys, k => k.StartsWith("postal_code:"));
    }
}
