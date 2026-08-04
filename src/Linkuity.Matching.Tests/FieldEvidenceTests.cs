using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Tests;

public class FieldEvidenceTests
{
    private static FieldEvidence Evidence(double m = 0.9, double u = 0.01, double? cap = null)
        => new() { SameEntityAgreement = m, ChanceAgreement = u, MaxAgreementBits = cap };

    [Fact]
    public void AgreementIsWorthLogOfTheLikelihoodRatio()
    {
        // log2(0.9 / 0.01) = 6.4919...
        Assert.Equal(6.4919, Evidence().AgreementBits, 4);
    }

    [Fact]
    public void DisagreementCarriesItsOwnWeight_AndIsNegative()
    {
        // log2(0.1 / 0.99) = -3.3074...
        Assert.Equal(-3.3074, Evidence().DisagreementBits, 4);
    }

    [Fact]
    public void PartialAgreementInterpolatesBetweenTheTwo()
    {
        var e = Evidence();
        Assert.Equal(e.DisagreementBits, e.EvidenceFor(0.0), 12);
        Assert.Equal(e.AgreementBits, e.EvidenceFor(1.0), 12);
        Assert.Equal((e.AgreementBits + e.DisagreementBits) / 2, e.EvidenceFor(0.5), 12);
    }

    [Fact]
    public void WeakAgreementIsNegativeEvidence_WithNothingTuned()
    {
        // The interpolation crosses zero at ~0.34 for these parameters. Worth asserting:
        // it means a mediocre match subtracts rather than adds, and nobody configured that.
        var e = Evidence();
        Assert.True(e.EvidenceFor(0.2) < 0);
        Assert.True(e.EvidenceFor(0.5) > 0);
    }

    [Fact]
    public void TheCapLimitsAgreementButNotDisagreement()
    {
        var e = Evidence(cap: 4.0);
        Assert.Equal(4.0, e.EvidenceFor(1.0), 12);
        Assert.Equal(e.DisagreementBits, e.EvidenceFor(0.0), 12);
    }

    [Theory]
    [InlineData(0.0, 0.01)]      // m at the boundary
    [InlineData(1.0, 0.01)]
    [InlineData(0.9, 0.0)]       // u at the boundary — infinite evidence
    [InlineData(0.9, 1.0)]
    [InlineData(double.NaN, 0.01)]
    public void ProbabilitiesOutsideTheOpenUnitInterval_AreRejected(double m, double u)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Evidence(m, u));

    [Fact]
    public void ChanceAgreementAboveMatchRate_IsRejectedOnFirstUse()
    {
        // Otherwise agreeing on the field is evidence AGAINST the match, which is never what the
        // author meant and is silent when it happens.
        //
        // Asserted on AgreementBits rather than on construction: the two probabilities are
        // init-only and set independently, so neither setter can see the other's final value and
        // the cross-check has to be lazy. The config loader forces it at load time (Task 2,
        // step 7) so a bad profile fails when it is read, not on whichever pair is scored first.
        var ex = Assert.Throws<ArgumentException>(() => Evidence(m: 0.2, u: 0.5).AgreementBits);
        Assert.Contains("chanceAgreement", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ANegativeOrNonFiniteCap_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Evidence(cap: -1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Evidence(cap: double.PositiveInfinity));
    }
}
