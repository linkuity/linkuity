using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

public class FingerprintBlockingTests
{
    private static readonly IBlockingStrategy Strategy = new FingerprintBlockingStrategy();

    private static IReadOnlyList<string> Keys(string organizationName)
        => Strategy.GenerateKeys(
            TestRecords.Person("r", new Dictionary<string, string> { ["organization_name"] = organizationName }),
            TestProfiles.Person);

    [Fact]
    public void Fingerprint_SuffixAndArticleVariants_ShareKey()
    {
        Assert.Equal(["fp:boeing"], Keys("THE BOEING COMPANY"));
        Assert.Equal(["fp:boeing"], Keys("BOEING CO"));
    }

    [Fact]
    public void Fingerprint_WordOrder_SortedAway()
    {
        Assert.Equal(["fp:disney walt"], Keys("THE WALT DISNEY COMPANY"));
        Assert.Equal(["fp:disney walt"], Keys("DISNEY WALT CO"));
    }

    [Fact]
    public void Fingerprint_DuplicateTokens_Deduped()
        => Assert.Equal(["fp:deluxe"], Keys("DELUXE DELUXE INC"));

    [Fact]
    public void Fingerprint_BlankValue_NoKey()
        => Assert.Empty(Keys("   "));

    [Fact]
    public void Fingerprint_UnregisteredSemanticTypes_NoKey()
    {
        // last_name has the Blocking role in the person profile but no canonicalizer is
        // registered for LastName, so fingerprint must stay silent.
        var keys = Strategy.GenerateKeys(
            TestRecords.Person("r", new Dictionary<string, string> { ["last_name"] = "Smith" }),
            TestProfiles.Person);
        Assert.Empty(keys);
    }

    [Fact]
    public void Fingerprint_HyphenVariant_CollidesWithJoinedForm()
    {
        Assert.Equal(["fp:mart wal", "fp:walmart"], Keys("WAL-MART INC").OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(["fp:walmart"], Keys("WALMART INC"));
    }

    [Fact]
    public void Fingerprint_HyphenSubsetName_EmitsBothVariants()
        => Assert.Equal(["fp:mart stores wal", "fp:stores walmart"],
            Keys("WAL-MART STORES INC").OrderBy(k => k, StringComparer.Ordinal));

    [Fact]
    public void Fingerprint_NonHyphenName_StillSingleKey()
        => Assert.Equal(["fp:disney walt"], Keys("THE WALT DISNEY COMPANY"));
}
