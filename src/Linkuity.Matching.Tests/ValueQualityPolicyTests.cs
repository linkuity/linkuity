using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Tests;

public class ValueQualityPolicyTests
{
    private static ValueQualityPolicy Policy(params string[] placeholders) => new(placeholders);

    [Fact]
    public void AnOrdinaryValue_IsEligibleForRarityWeighting()
        => Assert.True(Policy().IsRarityEligible("ACME HOLDINGS CORP"));

    [Theory]
    [InlineData("N/A")]
    [InlineData("n/a")]
    [InlineData("  UNKNOWN  ")]
    public void ADeclaredPlaceholder_IsNotEligible_RegardlessOfCaseOrPadding(string value)
        => Assert.False(Policy("N/A", "UNKNOWN").IsRarityEligible(value));

    [Fact]
    public void AnEmptyOrWhitespaceValue_IsNotEligible()
    {
        Assert.False(Policy().IsRarityEligible(""));
        Assert.False(Policy().IsRarityEligible("   "));
    }

    [Fact]
    public void ARepeatedSingleCharacter_IsNotEligible()
    {
        // "000-000-0000" and "XXXXXX" are rare precisely BECAUSE they are junk, which is the
        // failure rarity weighting would otherwise amplify.
        Assert.False(Policy().IsRarityEligible("000-000-0000"));
        Assert.False(Policy().IsRarityEligible("XXXXXXXX"));
        Assert.True(Policy().IsRarityEligible("ACME"));
    }

    [Fact]
    public void EligibilityIsTheOnlyThingAtStake_NotTheFieldsOrdinaryEvidence()
    {
        // Documents the contract rather than exercising code: a value failing quality keeps its
        // normal agreement evidence and loses only the rarity boost. Nothing in stage 1a reads
        // this, and the scorer must not consult it.
        Assert.False(Policy("N/A").IsRarityEligible("N/A"));
    }
}
