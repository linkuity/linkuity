using Linkuity.Core.Models;
using Linkuity.Core.Normalization;

namespace Linkuity.Core.Tests.Normalization;

/// <summary>
/// A phone number written without a country code cannot be resolved without knowing which
/// country it belongs to. The normalizer assumed the United States, so a national number from
/// anywhere else was either stamped +1 or left alone, with nothing recorded either way.
///
/// The failure is half-safe: a number invalid as US falls through unchanged, which is why the
/// assumption survived unnoticed. The dangerous case is a foreign number that happens to be
/// valid as US — silently rewritten into a different country's number.
/// </summary>
public class PhoneRegionTests
{
    // Ofcom's reserved fictional London range; valid as GB, not valid as US.
    private const string UkNational = "020 7946 0958";
    private const string UkE164 = "+442079460958";

    // NANP fictional range; valid as US.
    private const string UsNational = "(800) 555-0100";
    private const string UsE164 = "+18005550100";

    [Fact]
    public void DefaultRegion_IsUS_SoExistingBehaviourIsUnchanged()
        => Assert.Equal(UsE164, FieldNormalizer.Normalize(UsNational, SemanticFieldType.Phone));

    [Fact]
    public void NationalNumber_UsesTheConfiguredRegion()
        => Assert.Equal(UkE164, FieldNormalizer.Normalize(UkNational, SemanticFieldType.Phone, "GB"));

    [Fact]
    public void NationalNumber_ForeignToTheConfiguredRegion_IsLeftAlone()
        => Assert.Equal(UkNational, FieldNormalizer.Normalize(UkNational, SemanticFieldType.Phone, "US"));

    /// <summary>
    /// An explicit country code wins over the configured region — it is unambiguous, so the
    /// region must not override it.
    /// </summary>
    [Fact]
    public void ExplicitCountryCode_IgnoresTheConfiguredRegion()
        => Assert.Equal(UsE164, FieldNormalizer.Normalize("+1 800 555 0100", SemanticFieldType.Phone, "GB"));

    [Fact]
    public void RecordNormalizer_AppliesTheRegionFromSettings()
    {
        var settings = new NormalizationSettings(
            new Dictionary<string, SemanticFieldType>(StringComparer.OrdinalIgnoreCase)
            {
                ["phone"] = SemanticFieldType.Phone
            },
            PhoneRegion: "GB");

        var normalized = RecordNormalizer.NormalizeFields(
            new Dictionary<string, string> { ["phone"] = UkNational }, settings);

        Assert.Equal(UkE164, normalized["phone"]);
    }
}
