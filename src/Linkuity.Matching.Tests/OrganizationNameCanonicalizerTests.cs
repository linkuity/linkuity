using Linkuity.Matching.Canonicalization;

namespace Linkuity.Matching.Tests;

public class OrganizationNameCanonicalizerTests
{
    private static readonly OrganizationNameCanonicalizer Canonicalizer = new();

    private static IReadOnlyList<string> C(string value) => Canonicalizer.Canonicalize(value);

    // The four THE-X-COMPANY showcase missed pairs must collide after canonicalization.
    [Theory]
    [InlineData("The Boeing Company", new[] { "BOEING" })]
    [InlineData("BOEING CO", new[] { "BOEING" })]
    [InlineData("THE WALT DISNEY COMPANY", new[] { "WALT", "DISNEY" })]
    [InlineData("WALT DISNEY CO", new[] { "WALT", "DISNEY" })]
    [InlineData("THE COCA-COLA COMPANY", new[] { "COCA", "COLA" })]
    [InlineData("COCA-COLA CO", new[] { "COCA", "COLA" })]
    [InlineData("THE PROCTER & GAMBLE COMPANY", new[] { "PROCTER", "GAMBLE" })]
    [InlineData("PROCTER & GAMBLE CO", new[] { "PROCTER", "GAMBLE" })]
    [InlineData("MICROSOFT CORPORATION", new[] { "MICROSOFT" })]
    [InlineData("MICROSOFT CORP", new[] { "MICROSOFT" })]
    public void Canonicalize_SuffixArticleAndPunctuationVariants_Collide(string raw, string[] expected)
        => Assert.Equal(expected, C(raw));

    [Theory]
    [InlineData("AT&T Corp.", new[] { "ATT" })]
    [InlineData("AT & T CORP", new[] { "ATT" })]
    [InlineData("S&P GLOBAL INC", new[] { "SP", "GLOBAL" })]
    [InlineData("TEXAS A & M", new[] { "TEXAS", "AM" })]
    public void Canonicalize_CollapsesAmpersandInitials(string raw, string[] expected)
        => Assert.Equal(expected, C(raw));

    [Fact]
    public void Canonicalize_MultiLetterAmpersand_FoldsToTokenBreak()
        => Assert.Equal(new[] { "JOHNSON", "JOHNSON" }, C("JOHNSON & JOHNSON"));

    [Fact]
    public void Canonicalize_LeadingArticle_DroppedEvenWhenOneTokenRemains()
        => Assert.Equal(new[] { "GAP" }, C("THE GAP INC")); // plan deviation: >=1 remaining

    [Fact]
    public void Canonicalize_SingleLetterAfterArticle_KeepsArticle()
        => Assert.Equal(new[] { "A", "O", "SMITH" }, C("A O SMITH CORP"));

    [Fact]
    public void Canonicalize_DigitsSurvive()
        => Assert.Equal(new[] { "7", "ELEVEN" }, C("7-ELEVEN INC"));

    [Fact]
    public void Canonicalize_RepeatedTrailingSuffixes_StripToDistinctiveTokens()
        => Assert.Equal(new[] { "ACME" }, C("ACME HOLDINGS INC")); // HOLDINGS is on the list

    [Fact]
    public void Canonicalize_GroupIsNotASuffix()
        => Assert.Equal(new[] { "VOLKSWAGEN", "GROUP" }, C("VOLKSWAGEN GROUP"));

    [Fact]
    public void Canonicalize_MexicanSabDeCv_StripsViaRepetition()
        => Assert.Equal(new[] { "GRUPO", "TELEVISA" }, C("GRUPO TELEVISA S.A.B. DE C.V."));

    [Fact]
    public void Canonicalize_DeMidName_IsSafe()
        => Assert.Equal(new[] { "BANCO", "DE", "CHILE" }, C("BANCO DE CHILE"));

    [Fact]
    public void Canonicalize_PeriodsDeletedSoDottedSuffixStrips()
        => Assert.Equal(new[] { "TOTAL" }, C("TOTAL S.A."));

    [Fact]
    public void Canonicalize_ApostropheDeleted()
        => Assert.Equal(new[] { "OREILLY", "AUTOMOTIVE" }, C("O'Reilly Automotive Inc"));

    [Fact]
    public void Canonicalize_AllWeakTokens_NeverEmpty()
        => Assert.Equal(new[] { "COMPANY" }, C("THE COMPANY INC"));

    [Fact]
    public void Canonicalize_SuffixWordsMidName_Survive()
        => Assert.Equal(new[] { "CORPORATION", "TRUST", "CENTER" }, C("CORPORATION TRUST CENTER"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-")]
    [InlineData("...")]
    [InlineData("***")]
    public void Canonicalize_NoAlphanumericContent_ReturnsEmpty(string raw)
        => Assert.Empty(C(raw));

    // ---- 2c: Variants + CanonicalizeKeepingSuffixes ----

    [Fact]
    public void Variants_HyphenatedName_EmitsPrimaryAndJoined()
    {
        var variants = Canonicalizer.Variants("WAL-MART STORES INC");
        Assert.Equal(2, variants.Count);
        Assert.Equal(new[] { "WAL", "MART", "STORES" }, variants[0]);
        Assert.Equal(new[] { "WALMART", "STORES" }, variants[1]);
    }

    [Fact]
    public void Variants_NoHyphen_SingleVariant()
    {
        var variants = Canonicalizer.Variants("THE BOEING COMPANY");
        var only = Assert.Single(variants);
        Assert.Equal(new[] { "BOEING" }, only);
    }

    [Fact]
    public void Variants_HyphenWithIdenticalOutcome_SingleVariant()
    {
        // COCA-COLA vs COCACOLA differ, so two variants; but a name where joining changes
        // nothing must not duplicate. "X-" trailing hyphen: primary [X], joined [X].
        var variants = Canonicalizer.Variants("X- INC");
        var only = Assert.Single(variants);
        Assert.Equal(new[] { "X" }, only);
    }

    [Fact]
    public void Variants_PunctuationOnly_SinglePrimaryEmptyVariant()
    {
        var variants = Canonicalizer.Variants("-");
        var only = Assert.Single(variants);
        Assert.Empty(only);
    }

    [Fact]
    public void Variants_DefaultInterfaceImplementation_WrapsCanonicalize()
    {
        ITokenCanonicalizer fake = new SingleTokenCanonicalizer();
        var variants = fake.Variants("anything");
        var only = Assert.Single(variants);
        Assert.Equal(new[] { "FIXED" }, only);
    }

    private sealed class SingleTokenCanonicalizer : ITokenCanonicalizer
    {
        public IReadOnlyList<string> Canonicalize(string value) => ["FIXED"];
    }

    [Fact]
    public void KeepingSuffixes_RetainsTrailingLegalSuffix()
        => Assert.Equal(new[] { "SOUTHWESTERN", "BELL", "CORP" },
            Canonicalizer.CanonicalizeKeepingSuffixes("SOUTHWESTERN BELL CORP"));

    [Fact]
    public void KeepingSuffixes_StillDropsLeadingArticle()
        => Assert.Equal(new[] { "BOEING", "COMPANY" },
            Canonicalizer.CanonicalizeKeepingSuffixes("The Boeing Company"));

    [Fact]
    public void KeepingSuffixes_BlankInput_ReturnsEmpty()
        => Assert.Empty(Canonicalizer.CanonicalizeKeepingSuffixes("   "));
}
