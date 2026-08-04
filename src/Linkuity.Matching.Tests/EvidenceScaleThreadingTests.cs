using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Tests;

/// <summary>
/// Pins the fix to <see cref="MatchingEngine.Resolve(EntityRecord, IReadOnlyCollection{EntityRecord}, MatchingProfile)"/>:
/// it now resolves the scorer's own <see cref="ScoreScale"/> and threads it into
/// <see cref="Strategies.IDecisionStrategy.Decide"/>, so <see cref="Strategies.Defaults.ThresholdDecisionStrategy"/>
/// bands an evidence-scored profile's log-odds score against its own log-odds thresholds instead
/// of building <see cref="MatchThresholds"/> on the default <see cref="ScoreScale.UnitInterval"/>.
/// Before this fix, <c>ThresholdDecisionStrategy.Decide</c> called <c>profile.ThresholdsOn()</c>
/// with no scale argument, which defaults to UnitInterval; an "evidence" profile's
/// autoMatchThreshold/reviewThreshold (well above 1.0, being bits of log-odds evidence) then threw
/// <see cref="ArgumentOutOfRangeException"/> from <see cref="MatchThresholds"/>'s constructor on the
/// very first scored pair, inside <c>MatchingEngine.Resolve</c> itself — reachable from every
/// caller of <c>Resolve</c>, not only through <c>ThresholdDecisionStrategy</c> directly.
/// </summary>
public class EvidenceScaleThreadingTests
{
    private static MatchingProfile EvidenceProfile() => new()
    {
        ContentType = "organization",
        Fields =
        [
            new ProfileField
            {
                Name = "organization_name",
                SemanticType = SemanticFieldType.OrganizationName,
                Roles = FieldRole.Matchable,
                SimilarityEvaluator = "exact",
                Evidence = new FieldEvidence { SameEntityAgreement = 0.9, ChanceAgreement = 0.01, MaxAgreementBits = 6.0 }
            }
        ],
        NormalizationStrategy = "identity",
        BlockingStrategies = ["token"],
        CandidateRetrievalStrategy = "linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = "evidence",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 5.0,
        ReviewThreshold = 2.0
    };

    private static EntityRecord Record(string sourceRecordId, string organizationName)
        => TestRecords.Person(sourceRecordId, new Dictionary<string, string> { ["organization_name"] = organizationName });

    [Fact]
    public void Resolve_WithEvidenceScoredProfile_BandsOnLogOdds_InsteadOfThrowing()
    {
        var engine = MatchingDefaults.CreateEngine();
        var profile = EvidenceProfile();

        // Identical names -> exact similarity 1.0 -> full AgreementBits (capped at 6.0), which
        // clears the 5.0 auto threshold. Before the fix this line throws
        // ArgumentOutOfRangeException instead of returning a decision.
        var result = engine.Resolve(Record("a", "Acme Corp"), [Record("b", "Acme Corp")], profile);

        Assert.Equal(MatchDecision.AutoMatch, result.Decision);
        Assert.Equal(6.0, result.FinalScore, 6);
    }
}
