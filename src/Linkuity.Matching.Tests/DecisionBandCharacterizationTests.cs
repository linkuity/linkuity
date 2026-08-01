using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

/// <summary>
/// Pins how the engine turns a score into an outcome, before the six independent band
/// implementations are consolidated. Characterization, not specification: these record what the
/// code does today so a refactor that changes it fails loudly.
/// </summary>
public class DecisionBandCharacterizationTests
{
    private static readonly MatchingProfile Profile =
        DefaultMatchingProfileProvider.CreatePersonProfile() with
        {
            AutoMatchThreshold = 0.90,
            ReviewThreshold = 0.75
        };

    // ── ThresholdDecisionStrategy: the declared seam ─────────────────────────────
    //
    // Three bands, both comparisons inclusive. Worth pinning despite being unreachable in
    // production: it is the abstraction the engine advertises, its result is read only by
    // tests, and both production paths re-implement banding instead of calling it. Whatever
    // replaces it must agree at these boundaries.

    [Theory]
    [InlineData(1.00, MatchDecision.AutoMatch)]
    [InlineData(0.90, MatchDecision.AutoMatch)]   // at the auto threshold: inclusive
    [InlineData(0.8999, MatchDecision.Review)]
    [InlineData(0.75, MatchDecision.Review)]      // at the review threshold: inclusive
    [InlineData(0.7499, MatchDecision.NoMatch)]
    [InlineData(0.00, MatchDecision.NoMatch)]
    public void ThresholdDecisionStrategy_BandsInclusivelyOnBothThresholds(double score, MatchDecision expected)
        => Assert.Equal(expected, new ThresholdDecisionStrategy().Decide(score, Profile));

    /// <summary>
    /// It has no notion of "nothing to compare" — a pair that produced no signals scores 0 and
    /// is reported as NoMatch, indistinguishable from a pair that was compared and disagreed.
    /// Both audits DO draw that distinction (CorpusBand.NonComparable). Recorded because
    /// consolidation must decide deliberately which behaviour wins, rather than inheriting one.
    /// </summary>
    [Fact]
    public void ThresholdDecisionStrategy_CannotExpressNonComparable()
        => Assert.Equal(MatchDecision.NoMatch, new ThresholdDecisionStrategy().Decide(0.0, Profile));

    // ── MatchingEngine: a silent discard, not a band ─────────────────────────────

    /// <summary>
    /// The engine drops every candidate scoring below ReviewThreshold before the decision
    /// strategy sees anything (MatchingEngine.cs:51). This is the quietest of the six sites: it
    /// is not a classification but a deletion, and the discarded pairs leave no trace in the
    /// result — a caller cannot tell "no candidate was close" from "candidates existed and were
    /// dropped here".
    ///
    /// Two records sharing no field values score 0 and are therefore absent, not present-and-low.
    /// </summary>
    [Fact]
    public void MatchingEngine_DiscardsSubReviewCandidates_LeavingNoTrace()
    {
        var engine = MatchingDefaults.CreateEngine();
        var profile = Profile with { CandidateRetrievalStrategy = "linear" };

        var left = Record("a", new() { ["email"] = "alice@example.com", ["last_name"] = "Anderson" });
        var right = Record("b", new() { ["email"] = "bob@example.net", ["last_name"] = "Zabriskie" });

        var result = engine.Resolve(left, [right], profile);

        Assert.Empty(result.Candidates);
        Assert.Equal(0, result.FinalScore);
        Assert.Equal(MatchDecision.NoMatch, result.Decision);
    }

    /// <summary>
    /// The counterpart: a pair at or above ReviewThreshold survives the discard and appears as a
    /// candidate. Without this, the test above would also pass if the engine returned nothing at
    /// all.
    /// </summary>
    [Fact]
    public void MatchingEngine_KeepsCandidatesAtOrAboveReviewThreshold()
    {
        var engine = MatchingDefaults.CreateEngine();
        var profile = Profile with { CandidateRetrievalStrategy = "linear" };

        var fields = new Dictionary<string, string>
        {
            ["email"] = "carol.chen@example.com",
            ["first_name"] = "Carol",
            ["last_name"] = "Chen"
        };

        var result = engine.Resolve(Record("a", fields), [Record("b", new(fields))], profile);

        var candidate = Assert.Single(result.Candidates);
        Assert.True(candidate.Score >= profile.ReviewThreshold,
            $"expected a surviving candidate at or above {profile.ReviewThreshold}, scored {candidate.Score}");
    }

    private static EntityRecord Record(string sourceRecordId, Dictionary<string, string> fields)
        => new()
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.Empty,
            SourceId = Guid.Empty,
            IngestBatchId = Guid.Empty,
            SourceRecordId = sourceRecordId,
            Fields = fields,
            BlockingKeys = [],
            CreatedAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)
        };
}
