using Linkuity.Core.Models;
using Linkuity.Core.Normalization;

namespace Linkuity.Core.Tests.Normalization;

/// <summary>
/// E.164 has nowhere to put an extension, so normalizing to it discarded one silently. Two
/// people sharing a switchboard and distinguished only by their extensions arrived at the same
/// normalized number, and both built-in profiles declare phone an identifier — so an exact
/// agreement on that number floored the pair and merged them. Measured before this fix: two
/// such records merged at similarity 1.0 into one golden record.
///
/// The fix keeps E.164 for numbers without an extension, so nothing already normalized changes
/// shape, and appends RFC 3966's extension parameter when there is one. That preserves the
/// distinguishing detail without giving up canonicalization: "ext 42" and "x42" still reach the
/// same value, which simply refusing to normalize extension-bearing numbers would not achieve.
/// </summary>
public class PhoneExtensionTests
{
    // NANP fictional range, matching PhoneRegionTests.
    private const string Base = "(800) 555-0100";
    private const string BaseE164 = "+18005550100";

    [Fact]
    public void Extension_IsPreserved()
        => Assert.Equal($"{BaseE164};ext=42",
            FieldNormalizer.Normalize("800-555-0100 ext 42", SemanticFieldType.Phone));

    /// <summary>
    /// The defect this exists to prevent: two different people at one switchboard must not
    /// normalize to the same value, because phone is an identifier in the built-in profiles and
    /// an exact agreement there floors the pair to an auto-merge.
    /// </summary>
    [Fact]
    public void DifferentExtensions_DoNotCollapseToTheSameNumber()
    {
        var fortyTwo = FieldNormalizer.Normalize("800-555-0100 ext 42", SemanticFieldType.Phone);
        var ninetyNine = FieldNormalizer.Normalize("800-555-0100 ext 99", SemanticFieldType.Phone);
        Assert.NotEqual(fortyTwo, ninetyNine);
    }

    /// <summary>
    /// Canonicalization still has to happen, or the fix trades a wrong merge for a missed one:
    /// these are two spellings of one extension and must agree.
    /// </summary>
    [Theory]
    [InlineData("800-555-0100 ext 42")]
    [InlineData("800-555-0100 x42")]
    [InlineData("800-555-0100 ext. 42")]
    [InlineData("+1 800-555-0100 ext 42")]
    public void ExtensionSpellings_ReachTheSameValue(string written)
        => Assert.Equal($"{BaseE164};ext=42",
            FieldNormalizer.Normalize(written, SemanticFieldType.Phone));

    /// <summary>
    /// The regression guard that keeps this change zero-churn: a number carrying no extension
    /// must keep the exact E.164 form it has today, or every stored phone value, blocking key
    /// and sample expectation moves.
    /// </summary>
    [Theory]
    [InlineData("(800) 555-0100")]
    [InlineData("+1 800 555 0100")]
    [InlineData("8005550100")]
    public void NumbersWithoutAnExtension_KeepPlainE164(string written)
        => Assert.Equal(BaseE164, FieldNormalizer.Normalize(written, SemanticFieldType.Phone));

    [Fact]
    public void Unparseable_StillReturnsTheOriginal()
        => Assert.Equal("not-a-phone ext 42",
            FieldNormalizer.Normalize("not-a-phone ext 42", SemanticFieldType.Phone));

    /// <summary>
    /// An extension is meaningful only against the number it hangs off, so the region rules from
    /// PhoneRegionTests must keep applying: a national number foreign to the configured region is
    /// left alone whether or not it carries one.
    /// </summary>
    [Fact]
    public void ForeignNationalNumberWithExtension_IsLeftAlone()
        => Assert.Equal("020 7946 0958 ext 42",
            FieldNormalizer.Normalize("020 7946 0958 ext 42", SemanticFieldType.Phone, "US"));

    [Fact]
    public void ExtensionSurvivesTheConfiguredRegion()
        => Assert.Equal("+442079460958;ext=42",
            FieldNormalizer.Normalize("020 7946 0958 ext 42", SemanticFieldType.Phone, "GB"));

    [Fact]
    public void BaseNumberIsUnchangedByTheFix()
        => Assert.Equal(BaseE164, FieldNormalizer.Normalize(Base, SemanticFieldType.Phone));
}
