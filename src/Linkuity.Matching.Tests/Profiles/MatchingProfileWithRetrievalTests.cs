using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Xunit;

namespace Linkuity.Matching.Tests.Profiles;

public sealed class MatchingProfileWithRetrievalTests
{
    [Fact]
    public void WithCandidateRetrievalStrategy_ReplacesStrategy_PreservesEverythingElse()
    {
        var original = DefaultMatchingProfileProvider.CreatePersonProfile();

        var updated = original.WithCandidateRetrievalStrategy("blocking-linear");

        Assert.Equal("blocking-linear", updated.CandidateRetrievalStrategy);
        Assert.Equal("linear", original.CandidateRetrievalStrategy); // original untouched
        Assert.Same(original.Fields, updated.Fields);
        Assert.Equal(original.ContentType, updated.ContentType);
        Assert.Equal(original.BlockingStrategies, updated.BlockingStrategies);
        Assert.Equal(original.AutoMatchThreshold, updated.AutoMatchThreshold);
        Assert.Equal(original.ReviewThreshold, updated.ReviewThreshold);
        Assert.Equal(original.ReviewFloorGate, updated.ReviewFloorGate);
        Assert.Equal(original.IdentifierFloorGate, updated.IdentifierFloorGate);
    }

    [Fact]
    public void WithCandidateRetrievalStrategy_PreservesNonDefaultIdentifierFloorGate()
    {
        // A dropped property here would silently disable the corroboration gate on the batch
        // path only (BatchMatchingService forces blocking-linear through this clone), so assert
        // against a NON-default value — equality against the 0.35 default would pass either way.
        var original = new MatchingProfile
        {
            ContentType = "person",
            Fields = [new ProfileField { Name = "email", SemanticType = SemanticFieldType.Email, Roles = FieldRole.Matchable | FieldRole.Identifier }],
            NormalizationStrategy = "identity",
            BlockingStrategies = ["exact-value"],
            CandidateRetrievalStrategy = "linear",
            SimilarityStrategy = "field-weighted",
            ScoringStrategy = "identifier-weighted",
            DecisionStrategy = "threshold",
            ClusteringStrategy = "union-find",
            AutoMatchThreshold = 0.90,
            ReviewThreshold = 0.75,
            IdentifierFloorGate = 0.55
        };

        Assert.Equal(0.55, original.WithCandidateRetrievalStrategy("blocking-linear").IdentifierFloorGate);
    }
}
