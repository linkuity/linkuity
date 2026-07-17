using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
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
}
