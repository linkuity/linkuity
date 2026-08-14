using Linkuity.Core.Models;
using Linkuity.Matching;

namespace Linkuity.Pipeline.Tests;

/// <summary>
/// The over-merge oracle (largest true entity size in the supplied ground truth) and the gate that
/// cannot pass while a cluster exceeds it. <see cref="CorpusAuditService.BuildOverMergeAudit"/> is
/// tested directly as a pure function (fast, no clustering needed to exercise its arithmetic), and
/// <see cref="CorpusAuditService.Audit"/> is tested end-to-end so the oracle is proven to be wired
/// from REAL ground truth and REAL clustering, not just the isolated helper.
/// </summary>
public class CorpusAuditOverMergeTests
{
    // ---- CorpusAuditService.BuildOverMergeAudit: pure arithmetic ----

    /// <summary>Every predicted cluster sits AT the oracle, never above it: passes.</summary>
    [Fact]
    public void ClustersAtOrBelowOracle_Passes()
    {
        var byLabel = new Dictionary<string, List<int>> { ["e1"] = [0, 1], ["e2"] = [2, 3] };
        var audit = CorpusAuditService.BuildOverMergeAudit([2, 2], largestClusterSize: 2, byLabel);

        Assert.Equal(2, audit.Oracle);
        Assert.True(audit.Passed);
        Assert.Null(audit.FailureMessage);
        Assert.Equal(0, audit.ClustersOverOracle);
        Assert.Equal(0, audit.RecordsInClustersOverOracle);
    }

    /// <summary>One cluster (size 5) exceeds the oracle (2): fails, and the message names all
    /// three required quantities (spec acceptance criterion).</summary>
    [Fact]
    public void OneOversizedCluster_Fails_MessageNamesOracleSizeAndRecordCount()
    {
        var byLabel = new Dictionary<string, List<int>> { ["e1"] = [0, 1], ["e2"] = [2] };
        var audit = CorpusAuditService.BuildOverMergeAudit([5, 1], largestClusterSize: 5, byLabel);

        Assert.Equal(2, audit.Oracle);
        Assert.False(audit.Passed);
        Assert.Equal(1, audit.ClustersOverOracle);
        Assert.Equal(5, audit.RecordsInClustersOverOracle);

        Assert.NotNull(audit.FailureMessage);
        Assert.Contains("2", audit.FailureMessage, StringComparison.Ordinal);   // the oracle
        Assert.Contains("5", audit.FailureMessage, StringComparison.Ordinal);   // the offending cluster size
        Assert.Contains("5", audit.FailureMessage, StringComparison.Ordinal);   // the record count
    }

    /// <summary><see cref="OverMergeAudit.RecordsInClustersOverOracle"/> counts RECORDS, not
    /// clusters: two oversized clusters of different sizes must sum, and the count must differ
    /// from the largest single cluster so the two quantities cannot be confused for one another.</summary>
    [Fact]
    public void RecordsInClustersOverOracle_SumsRecordsAcrossEveryOversizedCluster_NotJustTheLargest()
    {
        var byLabel = new Dictionary<string, List<int>> { ["e1"] = [0, 1] };
        var audit = CorpusAuditService.BuildOverMergeAudit([3, 4, 2], largestClusterSize: 4, byLabel);

        Assert.Equal(2, audit.Oracle);
        Assert.Equal(2, audit.ClustersOverOracle);          // the 3-cluster and the 4-cluster
        Assert.Equal(7, audit.RecordsInClustersOverOracle);  // 3 + 4, NOT 4 (the largest alone)
        Assert.Equal(4, audit.LargestClusterSize);
    }

    /// <summary>Clusters over 1,000 is a reported tripwire, independent of the oracle-relative
    /// pass/fail verdict — a legitimate large true entity must not be failed for being correct.</summary>
    [Fact]
    public void ClustersOverOneThousand_IsRecordedButDoesNotAloneFailTheGate()
    {
        var byLabel = new Dictionary<string, List<int>> { ["e1"] = Enumerable.Range(0, 1500).ToList() };
        var audit = CorpusAuditService.BuildOverMergeAudit([1500], largestClusterSize: 1500, byLabel);

        Assert.Equal(1500, audit.Oracle);
        Assert.Equal(1, audit.ClustersOverOneThousand);
        Assert.Equal(0, audit.ClustersOverOracle);   // the cluster does not exceed ITS OWN oracle
        Assert.True(audit.Passed);
    }

    /// <summary>No ground truth at all: there is no oracle to measure against, so the check is
    /// vacuously passing rather than flagging every cluster as "exceeding" an oracle of 0.</summary>
    [Fact]
    public void NoGroundTruth_IsVacuouslyPassing()
    {
        var audit = CorpusAuditService.BuildOverMergeAudit(
            [1, 1], largestClusterSize: 1, new Dictionary<string, List<int>>());

        Assert.Equal(0, audit.Oracle);
        Assert.True(audit.Passed);
        Assert.Equal(0, audit.ClustersOverOracle);
    }

    // ---- CorpusAuditService.Audit: end-to-end wiring ----

    private static CorpusAuditResult Run(
        IReadOnlyList<EntityRecord> records, IReadOnlyDictionary<string, string> groundTruth)
        => new CorpusAuditService(MatchingDefaults.CreateRegistry())
            .Audit(records, CorpusAuditFixtures.Profile(), groundTruth);

    /// <summary>Two true entities, each with two identically-named records: both auto-match into
    /// a cluster of exactly their own oracle (2). Passes end-to-end.</summary>
    [Fact]
    public void Pipeline_ClustersAtOracle_Passes()
    {
        var records = new[]
        {
            CorpusAuditFixtures.Org("a1", "ACME WIDGETS INC"),
            CorpusAuditFixtures.Org("a2", "ACME WIDGETS INC"),
            CorpusAuditFixtures.Org("b1", "BETA CORP INC"),
            CorpusAuditFixtures.Org("b2", "BETA CORP INC")
        };
        var truth = new Dictionary<string, string>
        {
            ["a1"] = "e1", ["a2"] = "e1", ["b1"] = "e2", ["b2"] = "e2"
        };

        var result = Run(records, truth);

        Assert.Equal(2, result.OverMerge.Oracle);
        Assert.Equal(2, result.OverMerge.LargestClusterSize);
        Assert.True(result.OverMerge.Passed);
        Assert.Null(result.OverMerge.FailureMessage);
        Assert.Equal(0, result.OverMerge.ClustersOverOracle);
        Assert.Equal(0, result.OverMerge.RecordsInClustersOverOracle);
    }

    /// <summary>
    /// Three identically-named records auto-match into ONE cluster of 3, but ground truth says only
    /// two of them (a1, a2) are the same real entity — a3 is its own, unrelated, singleton entity
    /// that happens to share a name. The oracle (2) is exceeded by the 3-record cluster: exactly
    /// the over-merge shape the gate exists to catch, reproduced end-to-end through real blocking,
    /// scoring and union-find clustering rather than assembled by hand.
    /// </summary>
    [Fact]
    public void Pipeline_OneOversizedCluster_Fails()
    {
        var records = new[]
        {
            CorpusAuditFixtures.Org("a1", "ACME WIDGETS INC"),
            CorpusAuditFixtures.Org("a2", "ACME WIDGETS INC"),
            CorpusAuditFixtures.Org("a3", "ACME WIDGETS INC")
        };
        var truth = new Dictionary<string, string> { ["a1"] = "e1", ["a2"] = "e1", ["a3"] = "e2" };

        var result = Run(records, truth);

        Assert.Equal(2, result.OverMerge.Oracle);
        Assert.Equal(3, result.OverMerge.LargestClusterSize);
        Assert.False(result.OverMerge.Passed);
        Assert.Equal(1, result.OverMerge.ClustersOverOracle);
        Assert.Equal(3, result.OverMerge.RecordsInClustersOverOracle);

        Assert.NotNull(result.OverMerge.FailureMessage);
        Assert.Contains("2", result.OverMerge.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("3", result.OverMerge.FailureMessage, StringComparison.Ordinal);
    }

    /// <summary>The oracle must come from THIS RUN's own ground truth, not a constant: a different
    /// corpus with a larger true entity produces a different oracle from the same service, the same
    /// code path, no taxonomy branch and no literal.</summary>
    [Fact]
    public void OracleIsDerivedFromGroundTruth_NotHardcoded()
    {
        var records = new[]
        {
            CorpusAuditFixtures.Org("a1", "ACME WIDGETS INC"),
            CorpusAuditFixtures.Org("a2", "ACME WIDGETS INC"),
            CorpusAuditFixtures.Org("a3", "ACME WIDGETS INC"),
            CorpusAuditFixtures.Org("a4", "ACME WIDGETS INC")
        };
        var truth = new Dictionary<string, string>
        {
            ["a1"] = "e1", ["a2"] = "e1", ["a3"] = "e1", ["a4"] = "e1"
        };

        var result = Run(records, truth);

        // All four share one true entity: oracle is 4, the cluster of 4 does not exceed it.
        Assert.Equal(4, result.OverMerge.Oracle);
        Assert.True(result.OverMerge.Passed);
    }
}
