using Linkuity.Matching;
using Linkuity.Matching.Profiles;

namespace Linkuity.Pipeline.Tests;

/// <summary>
/// Pins the corpus audit's banding before the six independent band implementations are
/// consolidated. Characterization, not specification: these assert what the code does today so
/// that a refactor which changes it fails loudly rather than quietly.
///
/// The audit classifies into FOUR bands. Production classifies into three (durable) or two
/// (batch). The extra one is <c>NonComparable</c> — the pair produced no signals at all, so
/// there was nothing to score. That is a genuinely different statement from "we compared and
/// the score was low", and the audits are the only place in the system that makes it.
/// Consolidation must not quietly drop it.
/// </summary>
public class CorpusAuditBandCharacterizationTests
{
    private static readonly MatchingProfile Profile =
        DefaultMatchingProfileProvider.CreatePersonProfile() with
        {
            AutoMatchThreshold = 0.90,
            ReviewThreshold = 0.75
        };

    [Theory]
    [InlineData(1.00, CorpusBand.Auto)]
    [InlineData(0.90, CorpusBand.Auto)]      // at the auto threshold: inclusive
    [InlineData(0.8999, CorpusBand.Review)]
    [InlineData(0.75, CorpusBand.Review)]    // at the review threshold: inclusive
    [InlineData(0.7499, CorpusBand.NoMatch)]
    [InlineData(0.00, CorpusBand.NoMatch)]
    public void ComparablePair_BandsOnThresholds_Inclusively(double score, CorpusBand expected)
        => Assert.Equal(expected, CorpusAuditService.BandOf(score, comparable: true, Profile, ScoreScale.UnitInterval));

    /// <summary>
    /// A pair with no signals is NonComparable whatever its score — the score is meaningless
    /// rather than low. Note the score does not have to be zero for this to hold.
    /// </summary>
    [Theory]
    [InlineData(1.00)]
    [InlineData(0.80)]
    [InlineData(0.00)]
    public void NonComparablePair_IsNeverBandedByScore(double score)
        => Assert.Equal(CorpusBand.NonComparable, CorpusAuditService.BandOf(score, comparable: false, Profile, ScoreScale.UnitInterval));
}
