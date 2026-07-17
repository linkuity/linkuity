using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

public class BlockingStrategyParityTests
{
    private static readonly MatchingProfile Profile = TestProfiles.Person;

    public static IEnumerable<object[]> Records =>
    [
        [new Dictionary<string, string> { ["email"] = "alice@example.com", ["last_name"] = "Smith", ["first_name"] = "Alice" }, new[] { "email:aliceexamplecom", "name:smith" }],
        [new Dictionary<string, string> { ["phone"] = "+15551234567", ["full_name"] = "Mr. John Q Public" }, new[] { "name:public", "phone:15551234567" }],
        [new Dictionary<string, string> { ["organization_name"] = "Acme Holdings LLC", ["domain_name"] = "acme.com" }, new[] { "domain_name:acmecom", "name:llc" }],
        [new Dictionary<string, string> { ["date_of_birth"] = "1990-01-02", ["name"] = "Jane Doe" }, new[] { "date_of_birth:19900102", "name:doe" }],
        [new Dictionary<string, string> { ["first_name"] = "Bob" }, Array.Empty<string>()]
    ];

    [Theory]
    [MemberData(nameof(Records))]
    public void Union_OfDefaultBlockingStrategies_EqualsExpectedKeys(Dictionary<string, string> fields, string[] expected)
    {
        var record = TestRecords.Person("r", fields);
        IBlockingStrategy exact = new ExactValueBlockingStrategy();
        IBlockingStrategy token = new TokenNameBlockingStrategy();

        var engineKeys = exact.GenerateKeys(record, Profile)
            .Concat(token.GenerateKeys(record, Profile))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(expected, engineKeys);
    }

    [Fact]
    public void ExactValue_KeysOffSemanticType_NotLiteralFieldName()
    {
        // A non-literal field name carrying SemanticFieldType.Email with a Blocking
        // role must still produce an exact key — proving semantic-driven blocking.
        var profile = new MatchingProfile
        {
            ContentType = "person",
            Fields =
            [
                new ProfileField { Name = "work_email", SemanticType = SemanticFieldType.Email, Roles = FieldRole.Matchable | FieldRole.Blocking }
            ],
            NormalizationStrategy = "semantic-field",
            BlockingStrategies = ["exact-value"],
            CandidateRetrievalStrategy = "linear",
            SimilarityStrategy = "default",
            ScoringStrategy = "default",
            DecisionStrategy = "threshold",
            ClusteringStrategy = "union-find",
            AutoMatchThreshold = 0.90,
            ReviewThreshold = 0.75
        };
        var record = TestRecords.Person("r", new Dictionary<string, string> { ["work_email"] = "Alice@Example.com" });

        var keys = new ExactValueBlockingStrategy().GenerateKeys(record, profile);

        Assert.Equal(["work_email:aliceexamplecom"], keys);
    }
}
