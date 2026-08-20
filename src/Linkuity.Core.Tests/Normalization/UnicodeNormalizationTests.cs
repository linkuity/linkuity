using System.Text;
using Linkuity.Core.Models;
using Linkuity.Core.Normalization;

namespace Linkuity.Core.Tests.Normalization;

/// <summary>
/// The same character can be written two ways: composed, as one code point, or decomposed, as a
/// base letter followed by a combining mark. Unicode calls these canonically equivalent - they
/// are the same text - but they are different byte sequences, and nothing in the pipeline
/// reconciled them. Two sources disagreeing about which form they emit produced records that
/// never matched, silently, and the loss lands hardest on the non-English data F12 is about.
///
/// Measured before this fix: two records carrying the same accented name in the two forms did
/// not match.
///
/// The fix applies NFC at the ingest normalization seam, so both forms reach storage, blocking
/// and comparison as the same string.
///
/// Every value under test is written as an explicit escape rather than a literal character, so
/// that no editor, encoding conversion or copy-paste can quietly change what is being asserted.
/// </summary>
public class UnicodeNormalizationTests
{
    private const string Composed = "Jos\u00e9";     // e-acute as ONE code point
    private const string Decomposed = "Jose\u0301";  // e followed by COMBINING ACUTE

    [Fact]
    public void TheTwoFormsAreGenuinelyDifferentStrings()
        => Assert.NotEqual(Composed, Decomposed);    // guards the premise of every test below

    [Fact]
    public void DecomposedInput_IsComposed()
        => Assert.Equal(Composed, FieldNormalizer.Normalize(Decomposed, SemanticFieldType.LastName));

    [Fact]
    public void ComposedInput_IsUnchanged()
        => Assert.Equal(Composed, FieldNormalizer.Normalize(Composed, SemanticFieldType.LastName));

    /// <summary>The defect itself: the two spellings must become one value.</summary>
    [Fact]
    public void BothFormsReachTheSameValue()
        => Assert.Equal(
            FieldNormalizer.Normalize(Composed, SemanticFieldType.LastName),
            FieldNormalizer.Normalize(Decomposed, SemanticFieldType.LastName));

    /// <summary>
    /// It is a property of text, not of any one field, so it must apply to every semantic type -
    /// including the ones with no normalization rule of their own, which otherwise carry the
    /// inconsistency straight into blocking keys and stored identifiers.
    /// </summary>
    [Theory]
    [InlineData(SemanticFieldType.FirstName)]
    [InlineData(SemanticFieldType.LastName)]
    [InlineData(SemanticFieldType.FullName)]
    [InlineData(SemanticFieldType.OrganizationName)]
    [InlineData(SemanticFieldType.AddressLine)]
    [InlineData(SemanticFieldType.Sku)]
    [InlineData(SemanticFieldType.ProductName)]
    [InlineData(SemanticFieldType.SourceIdentifier)]
    public void EverySemanticType_ComposesTheSameWay(SemanticFieldType type)
        => Assert.Equal(
            FieldNormalizer.Normalize(Composed, type),
            FieldNormalizer.Normalize(Decomposed, type));

    /// <summary>
    /// NFC only. NFKC would also fold compatibility characters - the "fi" ligature into "fi", the
    /// circled digit into "1" - which decides that two genuinely different source values are one,
    /// and is exactly what F62 forbids. Canonical composition loses nothing, because the two
    /// forms are the same text by Unicode's own definition; compatibility folding is a judgement
    /// about meaning that nobody asked us to make.
    /// </summary>
    [Theory]
    [InlineData("\ufb01")]  // LATIN SMALL LIGATURE FI  - NFKC would yield "fi"
    [InlineData("\u2460")]  // CIRCLED DIGIT ONE        - NFKC would yield "1"
    [InlineData("\u00bd")]  // VULGAR FRACTION ONE HALF - NFKC would yield "1/2"
    public void CompatibilityCharacters_AreNotFolded(string value)
        => Assert.Equal(value, FieldNormalizer.Normalize(value, SemanticFieldType.ProductName));

    [Theory]
    [InlineData("Smith")]
    [InlineData("ACME Corp")]
    [InlineData("0012345678905")]
    public void AsciiValues_AreUntouched(string value)
        => Assert.Equal(value, FieldNormalizer.Normalize(value, SemanticFieldType.Sku));

    /// <summary>
    /// An unpaired surrogate is not valid Unicode and cannot be normalized. Consistent with how
    /// this class already treats an unparseable date or phone number, the raw value is kept
    /// rather than the call being allowed to throw partway through an ingest.
    /// </summary>
    [Fact]
    public void InvalidUnicode_IsPassedThroughRatherThanThrowing()
    {
        var lone = "abc\ud800def";
        Assert.Equal(lone, FieldNormalizer.Normalize(lone, SemanticFieldType.ProductName));
    }

    /// <summary>
    /// Composition has to happen before the type-specific rules, not after, or those rules run
    /// against text in whichever form it arrived: a name with a decomposed accent behind an
    /// honorific must come out both composed and stripped.
    /// </summary>
    [Fact]
    public void ComposesBeforeTypeSpecificRulesRun()
        => Assert.Equal(Composed, FieldNormalizer.Normalize("Dr. " + Decomposed, SemanticFieldType.LastName));

    [Fact]
    public void TheResultIsActuallyInFormC()
        => Assert.True(FieldNormalizer.Normalize(Decomposed, SemanticFieldType.LastName)
            .IsNormalized(NormalizationForm.FormC));
}
