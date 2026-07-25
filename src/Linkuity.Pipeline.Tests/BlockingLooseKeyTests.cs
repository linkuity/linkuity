using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Pipeline;

namespace Linkuity.Pipeline.Tests;

/// <summary>
/// The 2c robustness classes, pinned reachable under the shipping org strategy set with
/// the profile's maxBlockSize threaded through (suppression reporting active; blocks in
/// these two-record fixtures never exceed it). Each class also asserts its headline
/// strategy CONTRIBUTES to the pair via audit attribution, so reverting that strategy
/// fails the test even where older strategies also connect the pair.
/// </summary>
public class BlockingLooseKeyTests
{
    private static BlockingAuditService NewService() => new(MatchingDefaults.CreateRegistry());

    private static MatchingProfile OrgProfile => DefaultMatchingProfileProvider.CreateOrganizationProfile();

    private static EntityRecord Org(string id, string name) => new()
    {
        Id = Guid.NewGuid(), ProjectId = Guid.Empty, SourceId = Guid.Empty, IngestBatchId = Guid.Empty,
        SourceRecordId = id, Fields = new Dictionary<string, string> { ["organization_name"] = name },
        CreatedAt = DateTimeOffset.UnixEpoch
    };

    private static void AssertPairReachable(string leftName, string rightName, string mustContribute)
    {
        var records = new[] { Org("l", leftName), Org("r", rightName) };
        var groundTruth = new Dictionary<string, string> { ["l"] = "x", ["r"] = "x" };

        var result = NewService().Audit(records, OrgProfile, groundTruth,
            maxCandidates: null, maxBlockSize: OrgProfile.MaxBlockSize);

        Assert.NotNull(result.Suppression);
        var effective = result.Suppression!.EffectiveReachability!;
        Assert.Equal(1, effective.TrueMatchPairs);
        Assert.Empty(effective.MissedPairs);
        Assert.Equal(1.0, effective.Recall);
        // The class's headline strategy must be a real contributor: reverting it fails
        // this test even where other strategies also connect the pair.
        Assert.Contains(effective.Attribution,
            a => a.StrategyName == mustContribute && a.ReachablePairsContributed == 1);
    }

    [Fact]
    public void HyphenClass_WalmartSplitAndSubset_Reachable()
        => AssertPairReachable("WAL-MART STORES INC", "WALMART INC", mustContribute: "token");

    [Fact]
    public void SubsetReorderClass_SharedRareToken_Reachable()
        => AssertPairReachable("ZETA GLOBAL INC", "GLOBAL ZETA HOLDINGS", mustContribute: "token");

    [Fact]
    public void TypoClass_TrigramOverlap_Reachable()
        => AssertPairReachable("MICROSFT CORP", "MICROSOFT CORPORATION", mustContribute: "ngram");

    [Fact]
    public void AcronymClass_SbcSouthwesternBell_Reachable()
        => AssertPairReachable("SBC COMMUNICATIONS INC", "SOUTHWESTERN BELL CORP", mustContribute: "acronym");

    [Fact]
    public void OrgProfile_ShipsSixStrategiesWithMaxBlockSize()
    {
        Assert.Equal(["exact-value", "fingerprint", "phonetic", "token", "acronym", "ngram"],
            OrgProfile.BlockingStrategies);
        Assert.Equal(50, OrgProfile.MaxBlockSize);
    }
}
