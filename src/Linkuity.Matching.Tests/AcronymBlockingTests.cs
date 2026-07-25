using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

public class AcronymBlockingTests
{
    private static readonly IBlockingStrategy Strategy = new AcronymBlockingStrategy();

    private static IReadOnlyList<string> Keys(string organizationName)
        => Strategy.GenerateKeys(
            TestRecords.Person("r", new Dictionary<string, string> { ["organization_name"] = organizationName }),
            TestProfiles.Person);

    [Fact]
    public void Acronym_Generate_EmitsSuffixIncludingAndSuffixStrippedInitials()
    {
        var keys = Keys("SOUTHWESTERN BELL CORP");
        Assert.Contains("acr:sbc", keys); // S+B+C, the C from CORP (suffix-keeping form)
        Assert.Contains("acr:sb", keys);  // suffix-stripped form
    }

    [Fact]
    public void Acronym_Recognize_ShortAlphabeticTokenIsAPotentialAcronym()
        => Assert.Contains("acr:sbc", Keys("SBC COMMUNICATIONS INC"));

    [Fact]
    public void Acronym_TheShowcasePair_ShareAKey()
        => Assert.Contains("acr:sbc",
            Keys("SBC COMMUNICATIONS INC").Intersect(Keys("SOUTHWESTERN BELL CORP")));

    [Fact]
    public void Acronym_Generate_SkipsNamesWithMoreThanSixTokens()
    {
        var keys = Keys("ONE TWO THREE FOUR FIVE SIX SEVEN"); // 7 tokens, no suffixes
        Assert.DoesNotContain(keys, k => k.Length > "acr:".Length + 5); // no generated initials key
    }

    [Fact]
    public void Acronym_Recognize_SixLetterToken_NotRecognized()
        => Assert.DoesNotContain("acr:boeing", Keys("BOEING CO"));

    [Fact]
    public void Acronym_Recognize_DigitBearingToken_NotRecognized()
        => Assert.DoesNotContain(Keys("3M COMPANY"), k => k == "acr:3m");

    [Fact]
    public void Acronym_TwoTokenWithSuffix_EmitsOnlySuffixIncludingInitials()
    {
        var keys = Keys("MICROSOFT CORPORATION");
        Assert.Contains("acr:mc", keys);                        // MICROSOFT+CORPORATION initials
        Assert.DoesNotContain(keys, k => k == "acr:m");         // canonical [MICROSOFT] is 1 token: no initials
        Assert.DoesNotContain(keys, k => k == "acr:microsoft"); // 9 letters: not recognized
    }

    [Fact]
    public void Acronym_NonOrgFields_Silent()
    {
        var keys = Strategy.GenerateKeys(
            TestRecords.Person("r", new Dictionary<string, string> { ["last_name"] = "Smith" }),
            TestProfiles.Person);
        Assert.Empty(keys);
    }
}
