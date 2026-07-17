using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

public class MatchKeyTests
{
    [Theory]
    [InlineData("  Alice@Example.com ", "aliceexamplecom")]
    [InlineData("+1 (555) 123-4567", "15551234567")]
    [InlineData("1990-01-02", "19900102")]
    public void Normalize_StripsToLowercaseAlphanumeric(string input, string expected)
        => Assert.Equal(expected, MatchKey.Normalize(input));

    [Fact]
    public void Tokens_SplitsOnDurableDelimitersAndNormalizes()
        => Assert.Equal(["jane", "doe"], MatchKey.Tokens("Jane.Doe").ToList());

    [Fact]
    public void IsNonCanonicalField_MatchesIdAndSource()
    {
        Assert.True(MatchKey.IsNonCanonicalField("ID"));
        Assert.True(MatchKey.IsNonCanonicalField("source"));
        Assert.False(MatchKey.IsNonCanonicalField("email"));
    }

    [Fact]
    public void TokenSimilarity_IsJaccardOverCanonicalFields()
    {
        var left = new Dictionary<string, string> { ["name"] = "jane doe", ["source"] = "CRM" };
        var right = new Dictionary<string, string> { ["name"] = "jane smith", ["source"] = "Billing" };
        // tokens: {jane,doe} vs {jane,smith} -> intersection 1 / union 3
        Assert.Equal(1.0 / 3.0, MatchKey.TokenSimilarity(left, right), 10);
    }

    [Fact]
    public void SharedExact_ComparesNormalizedFieldValues()
    {
        var left = new Dictionary<string, string> { ["email"] = "Alice@Example.com" };
        var right = new Dictionary<string, string> { ["email"] = "alice@example.com" };
        Assert.True(MatchKey.SharedExact(left, right, "email"));
        Assert.False(MatchKey.SharedExact(left, right, "phone"));
    }
}
