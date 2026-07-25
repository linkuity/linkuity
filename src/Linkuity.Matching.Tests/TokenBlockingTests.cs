using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

public class TokenBlockingTests
{
    private static readonly IBlockingStrategy Strategy = new TokenBlockingStrategy();

    private static IReadOnlyList<string> Keys(string organizationName)
        => Strategy.GenerateKeys(
            TestRecords.Person("r", new Dictionary<string, string> { ["organization_name"] = organizationName }),
            TestProfiles.Person);

    [Fact]
    public void Token_EmitsEveryTokenOfEveryVariant()
        => Assert.Equal(["token:mart", "token:stores", "token:wal", "token:walmart"],
            Keys("WAL-MART STORES INC").OrderBy(k => k, StringComparer.Ordinal));

    [Fact]
    public void Token_HyphenSplitAndJoinedForms_ShareAKey()
        => Assert.Contains("token:walmart", Keys("WAL-MART STORES INC").Intersect(Keys("WALMART INC")));

    [Fact]
    public void Token_SingleLetterTokens_Skipped()
        => Assert.Equal(["token:smith"], Keys("A O SMITH CORP"));

    [Fact]
    public void Token_DigitBearingShortToken_Kept()
        => Assert.Equal(["token:3m"], Keys("3M COMPANY"));

    [Fact]
    public void Token_BlankValue_NoKeys()
        => Assert.Empty(Keys("   "));

    [Fact]
    public void Token_UnregisteredSemanticTypes_NoKeys()
    {
        var keys = Strategy.GenerateKeys(
            TestRecords.Person("r", new Dictionary<string, string> { ["last_name"] = "Smith" }),
            TestProfiles.Person);
        Assert.Empty(keys);
    }
}
