using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Pipeline;

namespace Linkuity.Pipeline.Tests;

public class BlockingAuditSuppressionTests
{
    // Legacy-style profile (token-name keys) so a junk suffix block is easy to build.
    private static readonly MatchingProfile OrgProfile = new()
    {
        ContentType = "organization",
        Fields =
        [
            new ProfileField
            {
                Name = "organization_name",
                SemanticType = SemanticFieldType.OrganizationName,
                Roles = FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking,
                SimilarityEvaluator = "jaccard",
                Weight = 4.0
            }
        ],
        NormalizationStrategy = "identity",
        BlockingStrategies = ["token-name", "prefix"],
        CandidateRetrievalStrategy = "blocking-linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = "identifier-weighted",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 0.41,
        ReviewThreshold = 0.31
    };

    private static BlockingAuditService NewService() => new(MatchingDefaults.CreateRegistry());

    private static EntityRecord Org(string id, string name) => new()
    {
        Id = Guid.NewGuid(), ProjectId = Guid.Empty, SourceId = Guid.Empty, IngestBatchId = Guid.Empty,
        SourceRecordId = id, Fields = new Dictionary<string, string> { ["organization_name"] = name },
        CreatedAt = DateTimeOffset.UnixEpoch
    };

    // acme-a/acme-b reach each other ONLY via the junk "name:inc" block (prefixes differ).
    // zeta-a/zeta-b also share the rare "prefix:zeta". junk-d is a second unrelated "INC" filler
    // record: with engine-parity suppression (corpus frequency = size-1), a plain max+1 block
    // stays active, so name:inc needs max+2 members to remain a suppressed block for these tests.
    private static readonly EntityRecord[] Records =
    [
        Org("acme-a", "ACMEWIDGETS INC"),
        Org("acme-b", "AJAX INC"),
        Org("junk-c", "OMEGA INC"),
        Org("junk-d", "DELTA INC"),
        Org("zeta-a", "ZETA GLOBAL INC"),
        Org("zeta-b", "ZETA HOLDINGS INC")
    ];

    private static readonly Dictionary<string, string> GroundTruth = new()
    {
        ["acme-a"] = "acme", ["acme-b"] = "acme",
        ["zeta-a"] = "zeta", ["zeta-b"] = "zeta"
    };

    [Fact]
    public void NoMaxBlockSize_NoSuppressionReport()
        => Assert.Null(NewService().Audit(Records, OrgProfile, GroundTruth).Suppression);

    [Fact]
    public void Suppression_ReportsSuppressedBlocks_AndDualCeiling()
    {
        // name:inc block = 6 members, corpus frequency 5 > 4 -> suppressed. prefix:zeta (2) stays active.
        var result = NewService().Audit(Records, OrgProfile, GroundTruth, maxCandidates: null, maxBlockSize: 4);

        Assert.NotNull(result.Suppression);
        var sup = result.Suppression!;
        Assert.Equal(4, sup.MaxBlockSize);
        var block = Assert.Single(sup.SuppressedBlocks);
        Assert.Equal("name:inc", block.Key);
        Assert.Equal(6, block.Size);

        // Raw ceiling: both pairs reachable (acme via name:inc, zeta via name:inc AND prefix:zeta).
        Assert.Equal(2, result.Reachability!.ReachablePairs);
        Assert.Equal(1.0, result.Reachability.Recall);

        // Effective ceiling: acme pair lost (its only shared key is suppressed); zeta survives via prefix.
        Assert.NotNull(sup.EffectiveReachability);
        var effective = sup.EffectiveReachability!;
        Assert.Equal(2, effective.TrueMatchPairs);
        Assert.Equal(1, effective.ReachablePairs);
        Assert.Equal(0.5, effective.Recall);
        var lost = Assert.Single(effective.MissedPairs);
        Assert.Equal("acme", lost.CanonicalKey);
    }

    [Fact]
    public void Suppression_ListsRecordsWithNoActiveKeys()
    {
        // junk-c's keys are name:inc (suppressed at 4) and prefix:omeg (size 1, active) -> NOT a singleton.
        // Suppress prefix keys too by dropping the threshold to 1: every name:inc member with a
        // unique prefix keeps that prefix (size 1 == threshold 1, active). So build the singleton
        // deliberately: three records sharing BOTH their token-name and prefix keys. With
        // engine-parity suppression (corpus frequency = size-1), prefix:omeg needs 3 members
        // (frequency 2 > 1) to stay suppressed for all of them.
        var twins = new[]
        {
            Org("twin-a", "OMEGA INC"),
            Org("twin-b", "OMEGA INC"),
            Org("twin-c", "OMEGA INC"),
            Org("solo", "ZETA GLOBAL INC")
        };

        var result = NewService().Audit(twins, OrgProfile, groundTruth: null, maxCandidates: null, maxBlockSize: 1);

        Assert.NotNull(result.Suppression);
        var sup = result.Suppression!;
        // name:inc (4, freq 3) and prefix:omeg (3, freq 2) exceed 1 -> twins have zero active keys.
        Assert.Equal(new[] { "twin-a", "twin-b", "twin-c" }, sup.NoActiveKeyRecordIds);
        Assert.Null(sup.EffectiveReachability); // no ground truth supplied
    }

    [Fact]
    public void Suppression_ThresholdAboveAllBlocks_IsConservative()
    {
        var result = NewService().Audit(Records, OrgProfile, GroundTruth, maxCandidates: null, maxBlockSize: 50);

        Assert.NotNull(result.Suppression);
        var sup = result.Suppression!;
        Assert.Empty(sup.SuppressedBlocks);
        Assert.Empty(sup.NoActiveKeyRecordIds);
        Assert.Equal(result.Reachability!.Recall, sup.EffectiveReachability!.Recall); // dual ceiling identical
    }

    [Fact]
    public void SuppressionBoundary_BlockOfMaxPlusOne_StaysActive_EngineParity()
    {
        // blocking-linear counts key frequency over a corpus that excludes the query
        // record: a full block of maxBlockSize+1 has per-query frequency == max and is
        // NOT suppressed by the engine. The audit must agree.
        // Three records share only "prefix:zeta" (their name tokens' last words differ, so
        // token-name keys don't merge them) -> a single block of size 3 with maxBlockSize: 2.
        var trio = new[]
        {
            Org("zeta-one", "ZETAONE LLC"),
            Org("zeta-two", "ZETATWO CO"),
            Org("zeta-three", "ZETATHREE GROUP")
        };
        var groundTruth = new Dictionary<string, string>
        {
            ["zeta-one"] = "zeta", ["zeta-two"] = "zeta"
        };

        var result = NewService().Audit(trio, OrgProfile, groundTruth, maxCandidates: null, maxBlockSize: 2);

        Assert.NotNull(result.Suppression);
        var sup = result.Suppression!;
        Assert.Empty(sup.SuppressedBlocks);

        Assert.NotNull(sup.EffectiveReachability);
        Assert.Equal(result.Reachability!.Recall, sup.EffectiveReachability!.Recall);
        Assert.Equal(result.Reachability.ReachablePairs, sup.EffectiveReachability.ReachablePairs);
    }
}
