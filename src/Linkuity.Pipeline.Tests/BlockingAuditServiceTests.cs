using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Pipeline;

namespace Linkuity.Pipeline.Tests;

public class BlockingAuditServiceTests
{
    // Explicit 3-strategy org profile (exact-value + token-name + prefix), matching the
    // company-resolution showcase. NOT the built-in "organization" profile, which is
    // BlockingStrategies = ["exact-value","token-name"] (no prefix) — these tests depend on prefix.
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
        BlockingStrategies = ["exact-value", "token-name", "prefix"],
        CandidateRetrievalStrategy = "blocking-linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = "identifier-weighted",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 0.41,
        ReviewThreshold = 0.31
    };

    private static BlockingAuditService NewService()
        => new(MatchingDefaults.CreateRegistry());

    private static EntityRecord Org(string id, string name, string address = "", string postal = "")
    {
        var fields = new Dictionary<string, string> { ["organization_name"] = name };
        if (address.Length > 0) fields["address_line"] = address;
        if (postal.Length > 0) fields["postal_code"] = postal;
        return new EntityRecord
        {
            Id = Guid.NewGuid(), ProjectId = Guid.Empty, SourceId = Guid.Empty, IngestBatchId = Guid.Empty,
            SourceRecordId = id, Fields = fields, CreatedAt = DateTimeOffset.UnixEpoch
        };
    }

    [Fact]
    public void Audit_GroupsKeysByStrategy_ForEachRecord()
    {
        var records = new[] { Org("a", "MICROSOFT CORP") };

        var result = NewService().Audit(records, OrgProfile);

        var rb = Assert.Single(result.PerRecord);
        Assert.Equal("a", rb.SourceRecordId);
        // token-name keys on the LAST token -> the legal suffix.
        Assert.Contains("name:corp", rb.KeysByStrategy["token-name"]);
        // prefix keys on the first 4 normalized chars.
        Assert.Contains("prefix:micr", rb.KeysByStrategy["prefix"]);
        Assert.Contains("name:corp", rb.AllKeys);
    }

    [Fact]
    public void Audit_BuildsBlocks_AndAttributesEmittingStrategy()
    {
        // Two corps collide on the token-name suffix block "name:corp" but not on prefix.
        var records = new[] { Org("a", "MICROSOFT CORP"), Org("b", "ORACLE CORP") };

        var result = NewService().Audit(records, OrgProfile);

        var suffixBlock = result.Blocks.Single(b => b.Key == "name:corp");
        Assert.Equal(2, suffixBlock.Size);
        Assert.Equal(new[] { "a", "b" }, suffixBlock.MemberSourceRecordIds);
        Assert.Contains("token-name", suffixBlock.StrategyNames);
    }

    [Fact]
    public void Audit_ComputesStructuralStats_CandidatePairsAndSingletons()
    {
        // a & b share "name:corp"; c shares nothing -> singleton.
        var records = new[] { Org("a", "MICROSOFT CORP"), Org("b", "ORACLE CORP"), Org("c", "APPLE INC") };

        var result = NewService().Audit(records, OrgProfile);

        Assert.Equal(1, result.Structural.TotalCandidatePairs);   // only (a,b)
        Assert.Equal(1, result.Structural.SingletonRecordCount);  // c
        Assert.Equal(2, result.Structural.MaxBlockSize);
    }

    [Fact]
    public void Audit_FlagsBlocksOverMaxCandidates_AsCapHazards()
    {
        var records = new[]
        {
            Org("a", "MICROSOFT CORP"), Org("b", "ORACLE CORP"), Org("c", "CISCO CORP")
        };

        var result = NewService().Audit(records, OrgProfile, maxCandidates: 2);

        var hazard = Assert.Single(result.CapHazards);
        Assert.Equal("name:corp", hazard.Key);
        Assert.Equal(3, hazard.Size);
    }

    [Fact]
    public void Audit_NoGroundTruth_LeavesReachabilityNull()
    {
        var result = NewService().Audit(new[] { Org("a", "MICROSOFT CORP") }, OrgProfile);
        Assert.Null(result.Reachability);
    }

    [Fact]
    public void Audit_WithGroundTruth_ComputesRecall_AndFlagsMissedPair()
    {
        // Two true entities:
        //   apple: two records that DO collide (prefix:appl + name:inc) -> reachable
        //   boeing: THE BOEING COMPANY vs BOEING CO -> prefix theb/boei, name company/co -> NO shared key
        var records = new[]
        {
            Org("apple-1", "APPLE INC"),
            Org("apple-2", "APPLE INCORPORATED"),
            Org("boe-gleif", "THE BOEING COMPANY"),
            Org("boe-sec", "BOEING CO")
        };
        var groundTruth = new Dictionary<string, string>
        {
            ["apple-1"] = "apple", ["apple-2"] = "apple",
            ["boe-gleif"] = "boeing", ["boe-sec"] = "boeing"
        };

        var report = NewService().Audit(records, OrgProfile, groundTruth).Reachability!;

        Assert.Equal(2, report.TrueMatchPairs);            // one apple pair, one boeing pair
        Assert.Equal(1, report.ReachablePairs);            // apple only
        Assert.Equal(0.5, report.Recall, 3);
        var missed = Assert.Single(report.MissedPairs);
        Assert.Equal("boeing", missed.CanonicalKey);
        Assert.Contains("prefix:theb", missed.LeftKeys.Concat(missed.RightKeys));
        Assert.Contains("prefix:boei", missed.LeftKeys.Concat(missed.RightKeys));
    }

    [Fact]
    public void Audit_Attribution_MarksLoadBearingStrategyUnique()
    {
        // apple-1/apple-2 share "prefix:appl" (prefix only) and "name:inc"/"name:incorporated" differ,
        // so the ONLY shared key comes from prefix -> prefix is uniquely load-bearing for this pair.
        var records = new[] { Org("apple-1", "APPLE INC"), Org("apple-2", "APPLE INCORPORATED") };
        var groundTruth = new Dictionary<string, string> { ["apple-1"] = "apple", ["apple-2"] = "apple" };

        var report = NewService().Audit(records, OrgProfile, groundTruth).Reachability!;

        var prefix = report.Attribution.Single(a => a.StrategyName == "prefix");
        Assert.Equal(1, prefix.ReachablePairsContributed);
        Assert.Equal(1, prefix.UniquelyReachablePairs);
        var tokenName = report.Attribution.Single(a => a.StrategyName == "token-name");
        Assert.Equal(0, tokenName.ReachablePairsContributed);
    }
}
