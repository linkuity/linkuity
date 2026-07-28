using Linkuity.Core.Models;
using Linkuity.Matching;

namespace Linkuity.Pipeline.Tests;

public class CorpusAuditCoverageTests
{
    private static CorpusAuditService NewService() => new(MatchingDefaults.CreateRegistry());

    private static readonly EntityRecord[] Records =
    [
        CorpusAuditFixtures.Org("a", "ACME WIDGETS INC"),
        CorpusAuditFixtures.Org("b", "ACME WIDGETS"),
        CorpusAuditFixtures.Org("c", "ZETA HOLDINGS")
    ];

    [Fact]
    public void GateMode_RejectsUnlabeledRecords_NamingThem()
    {
        var truth = new Dictionary<string, string> { ["a"] = "acme", ["b"] = "acme" };
        var ex = Assert.Throws<ArgumentException>(
            () => NewService().Audit(Records, CorpusAuditFixtures.Profile(), truth, null, gateMode: true));
        Assert.Contains("c", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GateMode_RejectsGroundTruthNamingAbsentRecords_NamingThem()
    {
        var truth = new Dictionary<string, string>
            { ["a"] = "acme", ["b"] = "acme", ["c"] = "zeta", ["ghost"] = "nowhere" };
        var ex = Assert.Throws<ArgumentException>(
            () => NewService().Audit(Records, CorpusAuditFixtures.Profile(), truth, null, gateMode: true));
        Assert.Contains("ghost", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GateMode_AcceptsExactIdSetEquality()
    {
        var truth = new Dictionary<string, string> { ["a"] = "acme", ["b"] = "acme", ["c"] = "zeta" };
        var result = NewService().Audit(Records, CorpusAuditFixtures.Profile(), truth, null, gateMode: true);
        Assert.Equal(0, result.Counts.UnlabeledRecordCount);
    }

    [Fact]
    public void ExploratoryMode_AllowsPartialLabelsAndReportsEndpointPairs()
    {
        var truth = new Dictionary<string, string> { ["a"] = "acme", ["b"] = "acme" };
        var result = NewService().Audit(Records, CorpusAuditFixtures.Profile(), truth);

        Assert.Equal(1, result.Counts.UnlabeledRecordCount);
        Assert.True(result.Counts.UnlabeledEndpointPairs >= 0);
    }
}
