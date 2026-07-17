using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

public class TokenNameProductBlockingTests
{
    private static MatchingProfile ProfileWithProductName() => new()
    {
        ContentType = "product",
        Fields = [ new ProfileField { Name = "product_name", SemanticType = SemanticFieldType.ProductName, Roles = FieldRole.Matchable | FieldRole.Blocking, SimilarityEvaluator = "fuzzy" } ],
        NormalizationStrategy = "identity",
        BlockingStrategies = ["token-name"],
        CandidateRetrievalStrategy = "linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = "identifier-weighted",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 0.90,
        ReviewThreshold = 0.75
    };

    [Fact]
    public void ProductNameBlockingField_EmitsLastTokenNameKey()
    {
        var record = new EntityRecord
        {
            Id = Guid.NewGuid(), SourceRecordId = "r",
            ProjectId = Guid.Empty,
            SourceId = Guid.Empty,
            IngestBatchId = Guid.Empty,
            Fields = new Dictionary<string, string> { ["product_name"] = "Widget Alpha" },
            BlockingKeys = [], CreatedAt = DateTimeOffset.UtcNow
        };
        var keys = new TokenNameBlockingStrategy().GenerateKeys(record, ProfileWithProductName());
        Assert.Contains("name:alpha", keys);
    }
}
