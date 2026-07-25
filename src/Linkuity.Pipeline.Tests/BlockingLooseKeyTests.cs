using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Pipeline;

namespace Linkuity.Pipeline.Tests;

/// <summary>
/// The 2c robustness classes, pinned reachable under the shipping org strategy set WITH
/// suppression active — data shapes the showcase lacks (hyphen splits, subset/reorder,
/// typos, acronyms) that loose keys + 2b make reachable.
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

    private static void AssertPairReachable(string leftName, string rightName)
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
    }

    [Fact]
    public void HyphenClass_WalmartSplitAndSubset_Reachable()
        => AssertPairReachable("WAL-MART STORES INC", "WALMART INC");

    [Fact]
    public void SubsetReorderClass_SharedRareToken_Reachable()
        => AssertPairReachable("ZETA GLOBAL INC", "GLOBAL ZETA HOLDINGS");

    [Fact]
    public void TypoClass_TrigramOverlap_Reachable()
        => AssertPairReachable("MICROSFT CORP", "MICROSOFT CORPORATION");

    [Fact]
    public void AcronymClass_SbcSouthwesternBell_Reachable()
        => AssertPairReachable("SBC COMMUNICATIONS INC", "SOUTHWESTERN BELL CORP");

    [Fact]
    public void OrgProfile_ShipsSixStrategiesWithMaxBlockSize()
    {
        Assert.Equal(["exact-value", "fingerprint", "phonetic", "token", "acronym", "ngram"],
            OrgProfile.BlockingStrategies);
        Assert.Equal(50, OrgProfile.MaxBlockSize);
    }
}
