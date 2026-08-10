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

    /// <summary>
    /// Locks the built-in profiles' fingerprints to the literal values produced BEFORE
    /// FieldEvidence/AliasGroup/MinClusterCohesion/MaxAutoClusterSize/PlaceholderValues were added
    /// to the canonical string (verified by hand against the pre-change code before committing
    /// this test). Neither built-in profile sets any of the five, so if this value ever changes,
    /// either a new key stopped being conditional on non-null/non-empty, or one of the five
    /// defaults changed — either way, every score already stored under these fingerprints would
    /// silently stop matching what re-fingerprinting the same profile produces.
    /// </summary>
    [Fact]
    public void BuiltInProfiles_FingerprintUnchangedByTheFiveNewSettings()
    {
        Assert.Equal("9355cbf5d83938de", ProfileFingerprint.Of(DefaultMatchingProfileProvider.CreatePersonProfile()));
        Assert.Equal("ca4feabd60296705", ProfileFingerprint.Of(DefaultMatchingProfileProvider.CreateOrganizationProfile()));
    }

    // ── The five new settings must change the fingerprint when set ──────────────

    [Fact]
    public void MinClusterCohesionChange_ChangesFingerprint()
        => Assert.NotEqual(
            ProfileFingerprint.Of(Base()),
            ProfileFingerprint.Of(Base() with { MinClusterCohesion = 0.5 }));

    [Fact]
    public void MaxAutoClusterSizeChange_ChangesFingerprint()
        => Assert.NotEqual(
            ProfileFingerprint.Of(Base()),
            ProfileFingerprint.Of(Base() with { MaxAutoClusterSize = 50 }));

    [Fact]
    public void PlaceholderValuesChange_ChangesFingerprint()
        => Assert.NotEqual(
            ProfileFingerprint.Of(Base()),
            ProfileFingerprint.Of(Base() with { PlaceholderValues = ["N/A", "UNKNOWN"] }));

    [Fact]
    public void PlaceholderValuesOrder_DoesNotChangeFingerprint()
    {
        var profile = Base() with { PlaceholderValues = ["N/A", "UNKNOWN"] };
        var reordered = profile with { PlaceholderValues = ["UNKNOWN", "N/A"] };

        Assert.Equal(ProfileFingerprint.Of(profile), ProfileFingerprint.Of(reordered));
    }

    [Fact]
    public void AliasGroupChange_ChangesFingerprint()
    {
        var profile = Base();
        var aliased = profile with
        {
            Fields = profile.Fields.Select((f, i) => i == 0 ? f with { AliasGroup = "g1" } : f).ToList()
        };

        Assert.NotEqual(ProfileFingerprint.Of(profile), ProfileFingerprint.Of(aliased));
    }

    [Fact]
    public void NullEquivalentsChange_ChangesFingerprint()
    {
        // A sentinel list changes which pairs are ever Compared vs Missing*, exactly like
        // blocking or normalization do -- the fingerprint must catch it.
        var profile = Base();
        var withSentinel = profile with
        {
            Fields = profile.Fields.Select((f, i) => i == 0 ? f with { NullEquivalents = ["8888", "UNKNOWN"] } : f).ToList()
        };

        Assert.NotEqual(ProfileFingerprint.Of(profile), ProfileFingerprint.Of(withSentinel));
    }

    [Fact]
    public void NullEquivalentsOrder_DoesNotChangeFingerprint()
    {
        var profile = Base();
        var a = profile with
        {
            Fields = profile.Fields.Select((f, i) => i == 0 ? f with { NullEquivalents = ["8888", "UNKNOWN"] } : f).ToList()
        };
        var b = profile with
        {
            Fields = profile.Fields.Select((f, i) => i == 0 ? f with { NullEquivalents = ["UNKNOWN", "8888"] } : f).ToList()
        };

        Assert.Equal(ProfileFingerprint.Of(a), ProfileFingerprint.Of(b));
    }

    [Fact]
    public void FieldEvidenceChange_ChangesFingerprint()
    {
        var profile = Base();
        var withEvidence = profile with
        {
            Fields = profile.Fields.Select((f, i) => i == 0
                ? f with { Evidence = new FieldEvidence { SameEntityAgreement = 0.9, ChanceAgreement = 0.1 } }
                : f).ToList()
        };

        Assert.NotEqual(ProfileFingerprint.Of(profile), ProfileFingerprint.Of(withEvidence));
    }
}
