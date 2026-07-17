using Linkuity.Core.Models;
using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

public class NormalizationStrategyTests
{
    private static readonly INormalizationStrategy Strategy = new SemanticFieldNormalizationStrategy();

    [Fact]
    public void Normalize_CanonicalizesMappedFieldsBySemanticType()
    {
        var record = TestRecords.Person("r", new Dictionary<string, string>
        {
            ["email"] = "Alice@Example.com",
            ["first_name"] = "Mr. John",
            ["id"] = "r" // unmapped -> unchanged
        });

        var result = Strategy.Normalize(record, TestProfiles.Person);

        Assert.Equal("alice@example.com", result.Fields["email"]);
        Assert.Equal("John", result.Fields["first_name"]);
        Assert.Equal("r", result.Fields["id"]);
    }

    [Fact]
    public void Normalize_IsIdempotentOnAlreadyNormalizedRecords()
    {
        var record = TestRecords.Person("r", new Dictionary<string, string>
        {
            ["email"] = "alice@example.com",
            ["first_name"] = "John"
        });

        var once = Strategy.Normalize(record, TestProfiles.Person);
        var twice = Strategy.Normalize(once, TestProfiles.Person);

        Assert.Equal(once.Fields["email"], twice.Fields["email"]);
        Assert.Equal(once.Fields["first_name"], twice.Fields["first_name"]);
        Assert.Equal(record.Fields["email"], once.Fields["email"]);
    }

    [Fact]
    public void Normalize_PreservesRecordIdentityAndBlockingKeys()
    {
        var record = TestRecords.Person("r", new Dictionary<string, string> { ["email"] = "a@b.com" }, ["email:abcom"]);
        var result = Strategy.Normalize(record, TestProfiles.Person);
        Assert.Equal(record.Id, result.Id);
        Assert.Equal(record.SourceRecordId, result.SourceRecordId);
        Assert.Equal(record.BlockingKeys, result.BlockingKeys);
    }
}
