using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Tests;

public class MatchingProfileTests
{
    [Fact]
    public void Profile_CarriesFieldRolesWeightsAndThresholds()
    {
        var profile = new MatchingProfile
        {
            ContentType = "person",
            Fields =
            [
                new ProfileField
                {
                    Name = "email",
                    SemanticType = SemanticFieldType.Email,
                    Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking,
                    SimilarityEvaluator = "exact",
                    Weight = 2.0
                }
            ],
            NormalizationStrategy = "semantic-field",
            BlockingStrategies = ["exact-value", "token-name"],
            CandidateRetrievalStrategy = "linear",
            SimilarityStrategy = "default",
            ScoringStrategy = "default",
            DecisionStrategy = "threshold",
            ClusteringStrategy = "union-find",
            AutoMatchThreshold = 0.90,
            ReviewThreshold = 0.75
        };

        var email = Assert.Single(profile.Fields);
        Assert.True(email.Roles.HasFlag(FieldRole.Blocking));
        Assert.True(email.Roles.HasFlag(FieldRole.Matchable));
        Assert.Equal(2.0, email.Weight);
        Assert.Equal("exact", email.SimilarityEvaluator);
        Assert.Equal(["exact-value", "token-name"], profile.BlockingStrategies);
        Assert.Equal(0.90, profile.AutoMatchThreshold);
        Assert.Equal(0.75, profile.ReviewThreshold);
    }

    [Fact]
    public void ProfileField_WeightDefaultsToOne()
    {
        var field = new ProfileField { Name = "phone", SemanticType = SemanticFieldType.Phone, Roles = FieldRole.Matchable };
        Assert.Equal(1.0, field.Weight);
    }
}
