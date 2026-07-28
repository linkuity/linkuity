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
        CorpusAuditFixtures.Org("orphan", "ZETA HOLDINGS")
    ];

    [Fact]
    public void GateMode_RejectsUnlabeledRecords_NamingThem()
    {
        var truth = new Dictionary<string, string> { ["a"] = "acme", ["b"] = "acme" };
        var ex = Assert.Throws<ArgumentException>(
            () => NewService().Audit(Records, CorpusAuditFixtures.Profile(), truth, null, gateMode: true));
        Assert.Contains("orphan", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GateMode_RejectsGroundTruthNamingAbsentRecords_NamingThem()
    {
        var truth = new Dictionary<string, string>
            { ["a"] = "acme", ["b"] = "acme", ["orphan"] = "zeta", ["ghost"] = "nowhere" };
        var ex = Assert.Throws<ArgumentException>(
            () => NewService().Audit(Records, CorpusAuditFixtures.Profile(), truth, null, gateMode: true));
        Assert.Contains("ghost", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GateMode_AcceptsExactIdSetEquality()
    {
        var truth = new Dictionary<string, string> { ["a"] = "acme", ["b"] = "acme", ["orphan"] = "zeta" };
        var result = NewService().Audit(Records, CorpusAuditFixtures.Profile(), truth, null, gateMode: true);
        Assert.Equal(0, result.Counts.UnlabeledRecordCount);
    }

    [Fact]
    public void ExploratoryMode_AllowsPartialLabelsAndReportsEndpointPairs()
    {
        var truth = new Dictionary<string, string> { ["a"] = "acme", ["b"] = "acme" };
        var result = NewService().Audit(Records, CorpusAuditFixtures.Profile(), truth);

        Assert.Equal(1, result.Counts.UnlabeledRecordCount);
        // Derivation: "a"/"b" ("ACME WIDGETS INC"/"ACME WIDGETS") share enough tokens to block and
        // score above AutoMatchThreshold, so they land in one predicted cluster; "orphan" ("ZETA
        // HOLDINGS") shares no tokens with either and blocks into no key with them, so it is an
        // isolated singleton cluster. Per-cluster contribution is labeled*(size-labeled) +
        // Choose2(size-labeled): {a,b} has labeled=2, size=2 -> 2*0 + Choose2(0) = 0; {orphan} has
        // labeled=0, size=1 -> 0*1 + Choose2(1) = 0. Total = 0 regardless of whether a/b actually
        // merge, since with only one unlabeled record (orphan) in a singleton cluster of its own,
        // no cluster ever has both a labeled and an unlabeled member.
        Assert.Equal(0, result.Counts.UnlabeledEndpointPairs);
    }
}
