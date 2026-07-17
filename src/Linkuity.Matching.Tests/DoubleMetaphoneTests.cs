using Linkuity.Matching.Phonetics;

namespace Linkuity.Matching.Tests;

public class DoubleMetaphoneTests
{
    [Theory]
    [InlineData("Smith", "Smyth")]
    [InlineData("Catherine", "Katherine")]
    [InlineData("Jon", "John")]
    [InlineData("Sara", "Sarah")]
    [InlineData("Gail", "Gayle")]
    public void Encode_NameVariants_SharePrimaryKey(string a, string b)
    {
        var ea = DoubleMetaphone.Encode(a);
        var eb = DoubleMetaphone.Encode(b);
        Assert.False(string.IsNullOrEmpty(ea.Primary));
        Assert.Equal(ea.Primary, eb.Primary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]
    [InlineData("!!!")]
    public void Encode_EmptyOrNonAlpha_ReturnsEmptyPair(string input)
        => Assert.Equal(("", ""), DoubleMetaphone.Encode(input));

    [Fact]
    public void Encode_IsDeterministic()
        => Assert.Equal(DoubleMetaphone.Encode("Schwarzenegger"), DoubleMetaphone.Encode("Schwarzenegger"));

    [Fact]
    public void Encode_PrimaryNeverExceedsFourChars()
        => Assert.True(DoubleMetaphone.Encode("Schwarzenegger").Primary.Length <= 4);

    [Fact]
    public void Encode_DistinctNames_DoNotAllCollapse()
    {
        // Sanity: the encoder discriminates — "Smith" and "Jones" differ.
        Assert.NotEqual(DoubleMetaphone.Encode("Smith").Primary, DoubleMetaphone.Encode("Jones").Primary);
    }

    [Fact]
    public void Encode_LongDegenerateInput_TerminatesAndStaysCapped()
    {
        // Guards against an infinite loop and confirms the 4-char cap holds even
        // when a single letter repeats far past the code length.
        var (primary, alternate) = DoubleMetaphone.Encode(new string('b', 1000));
        Assert.True(primary.Length <= 4);
        Assert.True(alternate.Length <= 4);
    }

    [Fact]
    public void Encode_ProducesAlternate_WhenSpellingImpliesTwoReadings()
    {
        // Names with 'CH' (e.g. "Bacher") yield a distinct alternate reading (K vs X).
        var e = DoubleMetaphone.Encode("Bacher");
        Assert.NotEqual(e.Primary, e.Alternate);
    }
}
