using Linkuity.Matching.Canonicalization;

namespace Linkuity.Pipeline.Tests;

public class CorpusAuditStratumTests
{
    private static Stratum Classify(string left, string right)
    {
        var c = new OrganizationNameCanonicalizer();
        return CorpusAuditService.ClassifyPair(c.Canonicalize(left), c.Canonicalize(right));
    }

    [Fact]
    public void IdenticalCanonicalTokens_IsS1()
        => Assert.Equal(Stratum.S1Identical, Classify("LIONSTONE FUND LTD", "Lionstone Fund, Ltd."));

    [Fact]
    public void ProperSubset_IsS2()
        => Assert.Equal(Stratum.S2Containment, Classify("ACME WIDGETS INC", "GLOBAL ACME WIDGETS INC"));

    [Fact]
    public void HighOverlapNeitherSubset_IsS3()
        => Assert.Equal(Stratum.S3StrongOverlap, Classify("ALPHA BETA GAMMA CORP", "ALPHA BETA DELTA CORP"));

    [Fact]
    public void LowOverlap_IsS4()
        => Assert.Equal(Stratum.S4WeakOverlap,
            Classify("ALPHA BETA GAMMA DELTA EPSILON CORP", "ALPHA ZULU YANKEE XRAY WHISKEY CORP"));

    [Fact]
    public void NoSharedToken_IsS5()
        => Assert.Equal(Stratum.S5Disjoint, Classify("NATOMAS LABS, INC.", "VILLA TECHNOLOGIES, INC."));

    /// <summary>The flag must consume the canonicalizer's OWN vocabulary. A second suffix list
    /// in the audit would drift from the matcher — the failure this instrument exists to catch.
    /// Exposed as a predicate, not the set, so the vocabulary stays immutable.</summary>
    [Theory]
    [InlineData("INC", true)]
    [InlineData("inc", true)]
    [InlineData("LLC", true)]
    [InlineData("WIDGETS", false)]
    [InlineData("", false)]
    public void IsLegalSuffixMatchesTheCanonicalizerVocabulary(string token, bool expected)
        => Assert.Equal(expected, OrganizationNameCanonicalizer.IsLegalSuffix(token));
}
