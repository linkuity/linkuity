using Linkuity.Matching.Profiles;
using Linkuity.Matching.Profiles.Configuration;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

/// <summary>
/// Three ways a profile or a value could produce a wrong score without producing an error.
/// Each is now rejected at the point it enters the system, because none of them was detectable
/// afterwards: the output was a plausible number, and a merge decision was made from it.
/// </summary>
public class BadInputRejectionTests
{
    private static string ProfileJson(string fields, string extra = "") => $$"""
        {
          "contentType": "bad-input-test",
          "fields": [{{fields}}],
          "normalizationStrategy": "identity",
          "blockingStrategies": ["exact-value"],
          "candidateRetrievalStrategy": "linear",
          "similarityStrategy": "field-weighted",
          "scoringStrategy": "identifier-weighted",
          "decisionStrategy": "threshold",
          "clusteringStrategy": "union-find",
          "autoMatchThreshold": 0.9,
          "reviewThreshold": 0.75{{extra}}
        }
        """;

    private static MatchingProfile Load(string json)
        => new MatchingProfileConfigLoader().LoadFromJson(json, MatchingDefaults.CreateRegistry());

    // ── Route 1: field weights ───────────────────────────────────────────────────

    [Theory]
    [InlineData(-1.0)]   // negative: divides the weighted average by less than it adds, pushing scores above 1
    [InlineData(0.0)]    // contributes nothing; remove the field instead of weighting it out
    public void NonPositiveWeight_Rejected(double weight)
    {
        var json = ProfileJson(
            $$"""{ "name": "email", "semanticType": "Email", "roles": ["Matchable"], "similarityEvaluator": "exact", "weight": {{weight}} }""");

        var ex = Assert.Throws<MatchingProfileConfigException>(() => Load(json));
        Assert.Contains("weight", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PositiveWeight_StillAccepted()
    {
        var profile = Load(ProfileJson(
            """{ "name": "email", "semanticType": "Email", "roles": ["Matchable"], "similarityEvaluator": "exact", "weight": 2.5 }"""));

        Assert.Equal(2.5, Assert.Single(profile.Fields).Weight);
    }

    // ── Route 2: incompatible similarity/scoring pairing ─────────────────────────

    /// <summary>
    /// `default` similarity emits whole-record aggregates — a shared blocking-key COUNT, exact
    /// flags, token overlap — named by the strategy rather than by profile fields. A per-field
    /// scorer finds no field called "shared-blocking-keys", weighs nothing, and returns a number
    /// unrelated to the records. It never threw; it just answered wrongly.
    /// </summary>
    [Fact]
    public void AggregateSimilarity_WithPerFieldScorer_Rejected()
    {
        var json = ProfileJson(
            """{ "name": "email", "semanticType": "Email", "roles": ["Matchable"], "similarityEvaluator": "exact" }""")
            .Replace("\"similarityStrategy\": \"field-weighted\"", "\"similarityStrategy\": \"default\"");

        var ex = Assert.Throws<MatchingProfileConfigException>(() => Load(json));
        Assert.Contains("cannot be combined", ex.Message);
    }

    [Fact]
    public void PerFieldSimilarity_WithAggregateScorer_Rejected()
    {
        var json = ProfileJson(
            """{ "name": "email", "semanticType": "Email", "roles": ["Matchable"], "similarityEvaluator": "exact" }""")
            .Replace("\"scoringStrategy\": \"identifier-weighted\"", "\"scoringStrategy\": \"default\"");

        Assert.Throws<MatchingProfileConfigException>(() => Load(json));
    }

    [Fact]
    public void MatchingShapes_StillAccepted()
    {
        var json = ProfileJson(
            """{ "name": "email", "semanticType": "Email", "roles": ["Matchable"], "similarityEvaluator": "exact" }""")
            .Replace("\"similarityStrategy\": \"field-weighted\"", "\"similarityStrategy\": \"default\"")
            .Replace("\"scoringStrategy\": \"identifier-weighted\"", "\"scoringStrategy\": \"default\"");

        Assert.Equal("default", Load(json).ScoringStrategy);
    }

    // ── Route 3: non-finite numeric values ───────────────────────────────────────

    /// <summary>
    /// double.TryParse accepts these literals. NaN then loses every comparison including
    /// equality, propagates through the arithmetic, and is returned as a similarity — which
    /// fails every threshold test and lands the pair silently in no-match.
    /// </summary>
    [Theory]
    [InlineData("NaN", "5")]
    [InlineData("5", "NaN")]
    [InlineData("Infinity", "5")]
    [InlineData("-Infinity", "5")]
    public void NonFiniteNumericValue_IsNonComparable_NotASimilarity(string left, string right)
    {
        var field = new ProfileField
        {
            Name = "amount",
            SemanticType = Core.Models.SemanticFieldType.Sku,
            Roles = FieldRole.Matchable,
            SimilarityEvaluator = "numeric",
            Weight = 1.0
        };

        Assert.Null(new NumericSimilarityEvaluator().Evaluate(left, right, field));
    }

    [Fact]
    public void OrdinaryNumbers_StillCompare()
    {
        var field = new ProfileField
        {
            Name = "amount",
            SemanticType = Core.Models.SemanticFieldType.Sku,
            Roles = FieldRole.Matchable,
            SimilarityEvaluator = "numeric",
            Weight = 1.0
        };

        Assert.Equal(1.0, new NumericSimilarityEvaluator().Evaluate("42", "42", field));
    }
}
