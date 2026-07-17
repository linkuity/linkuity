using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Tests;

internal static class TestProfiles
{
    public static MatchingProfile Person => new()
    {
        ContentType = "person",
        Fields =
        [
            new ProfileField { Name = "first_name", SemanticType = SemanticFieldType.FirstName, Roles = FieldRole.Searchable | FieldRole.Matchable },
            new ProfileField { Name = "last_name", SemanticType = SemanticFieldType.LastName, Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking },
            new ProfileField { Name = "full_name", SemanticType = SemanticFieldType.FullName, Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking },
            new ProfileField { Name = "name", SemanticType = SemanticFieldType.FullName, Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking },
            new ProfileField { Name = "email", SemanticType = SemanticFieldType.Email, Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking },
            new ProfileField { Name = "phone", SemanticType = SemanticFieldType.Phone, Roles = FieldRole.Matchable | FieldRole.Blocking },
            new ProfileField { Name = "date_of_birth", SemanticType = SemanticFieldType.DateOfBirth, Roles = FieldRole.Matchable | FieldRole.Blocking },
            new ProfileField { Name = "domain_name", SemanticType = SemanticFieldType.DomainName, Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking },
            new ProfileField { Name = "organization_name", SemanticType = SemanticFieldType.OrganizationName, Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking }
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
}
