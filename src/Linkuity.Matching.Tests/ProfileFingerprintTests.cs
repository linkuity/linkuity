using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Tests;

/// <summary>
/// A fingerprint is only useful if it distinguishes exactly the changes that alter outcomes.
/// Too coarse and it certifies a sameness that does not hold; too fine and every cosmetic edit
/// looks like a rule change and the signal is ignored.
/// </summary>
public class ProfileFingerprintTests
{
    private static MatchingProfile Base() => DefaultMatchingProfileProvider.CreatePersonProfile();

    [Fact]
    public void SameProfile_SameFingerprint()
        => Assert.Equal(ProfileFingerprint.Of(Base()), ProfileFingerprint.Of(Base()));

    [Fact]
    public void Fingerprint_IsShortAndStableInShape()
    {
        var fingerprint = ProfileFingerprint.Of(Base());
        Assert.Equal(16, fingerprint.Length);
        Assert.All(fingerprint, c => Assert.True(char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f')));
    }

    // ── Changes that alter outcomes must change the fingerprint ──────────────────

    [Fact]
    public void ThresholdChange_ChangesFingerprint()
        => Assert.NotEqual(
            ProfileFingerprint.Of(Base()),
            ProfileFingerprint.Of(Base() with { AutoMatchThreshold = 0.95 }));

    [Fact]
    public void IdentifierFloorGateChange_ChangesFingerprint()
        => Assert.NotEqual(
            ProfileFingerprint.Of(Base()),
            ProfileFingerprint.Of(Base() with { IdentifierFloorGate = 0.5 }));

    /// <summary>
    /// Blocking decides which pairs are ever compared, so a change there alters results without
    /// touching a threshold. A fingerprint covering only scoring settings would miss it.
    /// </summary>
    [Fact]
    public void BlockingStrategyChange_ChangesFingerprint()
        => Assert.NotEqual(
            ProfileFingerprint.Of(Base()),
            ProfileFingerprint.Of(Base() with { BlockingStrategies = ["exact-value"] }));

    /// <summary>Normalization changes the values compared, for the same reason.</summary>
    [Fact]
    public void NormalizationChange_ChangesFingerprint()
        => Assert.NotEqual(
            ProfileFingerprint.Of(Base()),
            ProfileFingerprint.Of(Base() with { NormalizationStrategy = "semantic-field" }));

    [Fact]
    public void FieldWeightChange_ChangesFingerprint()
    {
        var reweighted = Base();
        var fields = reweighted.Fields.Select((f, i) => i == 0 ? f with { Weight = f.Weight + 1 } : f).ToList();

        Assert.NotEqual(
            ProfileFingerprint.Of(reweighted),
            ProfileFingerprint.Of(reweighted with { Fields = fields }));
    }

    [Fact]
    public void PhoneRegionChange_ChangesFingerprint()
        => Assert.NotEqual(
            ProfileFingerprint.Of(Base()),
            ProfileFingerprint.Of(Base() with { DefaultPhoneRegion = "GB" }));

    // ── Changes that alter nothing must NOT change it ────────────────────────────

    /// <summary>
    /// The engine unions the keys every blocking strategy produces, so listing them in a
    /// different order is the same configuration. Treating it as a rule change would train
    /// readers to ignore fingerprint differences.
    /// </summary>
    [Fact]
    public void BlockingStrategyOrder_DoesNotChangeFingerprint()
    {
        var profile = Base();
        var reversed = profile with { BlockingStrategies = profile.BlockingStrategies.Reverse().ToList() };

        Assert.Equal(ProfileFingerprint.Of(profile), ProfileFingerprint.Of(reversed));
    }

    /// <summary>Field declaration order does not affect scoring either.</summary>
    [Fact]
    public void FieldOrder_DoesNotChangeFingerprint()
    {
        var profile = Base();
        var reordered = profile with { Fields = profile.Fields.Reverse().ToList() };

        Assert.Equal(ProfileFingerprint.Of(profile), ProfileFingerprint.Of(reordered));
    }

    /// <summary>
    /// Two different built-in profiles must not collide — the case the fingerprint exists to
    /// tell apart.
    /// </summary>
    [Fact]
    public void DifferentProfiles_DifferentFingerprints()
        => Assert.NotEqual(
            ProfileFingerprint.Of(DefaultMatchingProfileProvider.CreatePersonProfile()),
            ProfileFingerprint.Of(DefaultMatchingProfileProvider.CreateOrganizationProfile()));
}
