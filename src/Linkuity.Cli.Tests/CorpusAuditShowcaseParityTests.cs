using System.Globalization;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Pipeline;

namespace Linkuity.Cli.Tests;

/// <summary>
/// Spec §11 self-validation. CorpusAuditService is a measurement instrument for ~1M records; the
/// instrument must reproduce a known-good result — the published 107-record company-resolution
/// scorecard — before any of its numbers are trusted. If these fail, the harness is wrong, not
/// the showcase: do not adjust an expected value to match observed output.
///
/// The corpus and ground truth are read through the CLI's own readers so the audit sees exactly
/// what `match corpus audit` feeds it. Audit is called at service level with gateMode: true,
/// which needs no CLI flags.
/// </summary>
public class CorpusAuditShowcaseParityTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Linkuity.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private static string Path_(params string[] parts) => Path.Combine([RepoRoot(), .. parts]);

    private static string CompaniesCsv() => Path_("showcases", "company-resolution", "run", "companies.csv");

    private static string GroundTruthCsv() => Path_("showcases", "company-resolution", "validate", "ground-truth.csv");

    private static MatchingProfile Profile() => ProfileResolver.ResolveNameOrFile(
        Path_("showcases", "company-resolution", "run", "company.profile.json"));

    private static CorpusAuditResult RunShowcase()
    {
        var records = AuditCliCommon.ReadCsv(CompaniesCsv());
        var truth = CorpusAuditCommands.ReadGroundTruth(GroundTruthCsv());

        return new CorpusAuditService(MatchingDefaults.CreateRegistry())
            .Audit(records, Profile(), truth, maxBlockSize: null, gateMode: true);
    }

    /// <summary>
    /// Unordered pair key. The audit orients pairs by CORPUS INDEX (companies.csv row order) while
    /// BatchMatchingService orients them by ORDINAL id order (BatchMatchingService.cs:71), so the
    /// two sides disagree on orientation for any pair whose ids sort against file order. Keying
    /// both by the same ordinal-sorted tuple is what stops the parity loop from silently skipping
    /// those pairs and "passing" on a fraction of the evidence.
    /// </summary>
    private static (string, string) PairKey(string a, string b)
        => string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

    /// <summary>
    /// "Correctly unified" in the SHOWCASE's sense (Test-Resolution.ps1:45-48): a ground-truth
    /// company is unified when every one of its records landed in exactly ONE predicted cluster,
    /// and left-separate when its records span more than one. Equivalently, over the audit's true
    /// pairs: a company is split iff any pair of its records ended in different clusters.
    ///
    /// This is NOT <see cref="CorpusAuditClusterSummary.UnifiedClusterCount"/>, which counts
    /// PREDICTED CLUSTERS with more than one member. The two are different quantities that both
    /// answer to the word "unified" and land near each other on this corpus (46 vs 50) — exactly
    /// the kind of near-miss that would let a conflation ship unnoticed, so both are pinned below.
    /// </summary>
    private static (int Unified, int LeftSeparate) ShowcaseUnifiedSplit(
        CorpusAuditResult result, IReadOnlyDictionary<string, string> truth)
    {
        var splitKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in result.AllTruePairs)
            if (!pair.SameCluster) splitKeys.Add(truth[pair.LeftSourceRecordId]);

        var allKeys = new HashSet<string>(truth.Values, StringComparer.Ordinal);
        return (allKeys.Count - splitKeys.Count, splitKeys.Count);
    }

    /// <summary>
    /// The complete published scorecard, not a selected pair of numbers. From
    /// docs/superpowers/specs/2026-07-28-missed-merge-detection-design.md §11 and
    /// showcases/company-resolution/README.md:
    ///
    ///   golden records 52   unified 46   left separate 3   incorrect 0
    ///   cluster precision 100.0%   post-cluster pairwise recall 88.9%   F1 94.1%
    ///
    /// over 107 records / 49 companies / 72 true pairs. The arithmetic closes on itself: 49
    /// companies, 3 of them split in two, gives 49 + 3 = 52 predicted clusters, so the golden
    /// count and the unified/separate split corroborate rather than merely coexist.
    /// </summary>
    [Fact]
    public void ReproducesTheCompletePublishedScorecard()
    {
        var truth = CorpusAuditCommands.ReadGroundTruth(GroundTruthCsv());
        var result = RunShowcase();

        Assert.Equal(107, result.Counts.Records);
        Assert.Equal(49, truth.Values.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(72L, result.Counts.TruePairs);
        Assert.Equal(52, result.ClusterSummary.GoldenRecordCount);

        var (unified, leftSeparate) = ShowcaseUnifiedSplit(result, truth);
        Assert.Equal(46, unified);
        Assert.Equal(3, leftSeparate);
        Assert.Equal(result.ClusterSummary.GoldenRecordCount, unified + 2 * leftSeparate);

        // "incorrect 0": no predicted cluster mixes two companies, which is what 100.0% cluster
        // pairwise precision means here.
        Assert.Equal(1.0, result.Metrics.ClusterPairwisePrecision, precision: 4);
        Assert.Equal(0.889, result.Metrics.PostClusterPairwiseRecall, precision: 3);
        Assert.Equal(64L, result.Counts.TruePositive);

        var p = result.Metrics.ClusterPairwisePrecision;
        var r = result.Metrics.PostClusterPairwiseRecall;
        Assert.Equal(0.941, 2 * p * r / (p + r), precision: 3);
    }

    /// <summary>
    /// Pins <see cref="CorpusAuditClusterSummary"/>'s own vocabulary so it can never be silently
    /// read as the showcase's "unified 46". UnifiedClusterCount counts predicted clusters with
    /// more than one member; on this corpus that is 50, not 46, and the difference is fully
    /// explained: 52 clusters = 49 companies + 3 splits, of which 2 shed a lone record into a
    /// cluster of its own.
    /// </summary>
    [Fact]
    public void ClusterSummaryCountsPredictedClustersNotUnifiedCompanies()
    {
        var result = RunShowcase();
        var summary = result.ClusterSummary;

        Assert.Equal(50, summary.UnifiedClusterCount);
        Assert.Equal(2, summary.SingletonCount);
        Assert.Equal(summary.GoldenRecordCount, summary.UnifiedClusterCount + summary.SingletonCount);

        var truth = CorpusAuditCommands.ReadGroundTruth(GroundTruthCsv());
        Assert.NotEqual(ShowcaseUnifiedSplit(result, truth).Unified, summary.UnifiedClusterCount);
    }

    /// <summary>
    /// direct_auto_recall MUST differ from post_cluster_pairwise_recall. Reproducing both is what
    /// proves the two metrics are not conflated — that conflation is the defect that made an
    /// earlier revision of this design unimplementable. 63/72 direct-auto against 64/72
    /// post-cluster: transitive closure through the clusters recovers pairs no single edge scored.
    ///
    /// 63 is not carried from the spec; it was re-measured with
    ///   match scoring audit --input showcases/company-resolution/run/companies.csv
    ///     --ground-truth showcases/company-resolution/validate/ground-truth.csv
    ///     --profile showcases/company-resolution/run/company.profile.json
    /// whose miss decomposition reads "72 true pairs -> auto 63, unreachable 8, non-comparable 0,
    /// review 0, below-review 1".
    /// </summary>
    [Fact]
    public void DirectAutoRecallDiffersFromPostClusterRecall()
    {
        var result = RunShowcase();

        Assert.True(result.Metrics.DirectAutoRecall < result.Metrics.PostClusterPairwiseRecall,
            $"direct_auto_recall ({result.Metrics.DirectAutoRecall}) must be strictly below " +
            $"post_cluster_pairwise_recall ({result.Metrics.PostClusterPairwiseRecall}); equality " +
            "would mean the two metrics are being conflated.");
        Assert.Equal(63L, result.Counts.DirectAutoTruePairs);
        Assert.Equal(63.0 / 72.0, result.Metrics.DirectAutoRecall, precision: 6);
    }

    /// <summary>
    /// Spec §6.2: PER-PAIR score equality with the batch path, not merely matching aggregates —
    /// two aggregates can agree while individual pairs differ in compensating ways.
    ///
    /// BuildMatchesCsv emits ONLY pairs at or above AutoMatchThreshold (BatchMatchingService.cs:66,
    /// pinned by BatchPairScoringCharacterizationTests.OmitsPairsBelowAutoThreshold), so the
    /// comparison is over auto-band pairs: every batch-emitted pair that is also a ground-truth
    /// pair must be Auto here with the IDENTICAL score.
    ///
    /// A loop that silently compares zero pairs would pass and prove nothing, so the count of
    /// compared pairs is pinned — and pinned to the audit's OWN direct-auto count, which means the
    /// loop is proven to have covered every true pair the audit banded Auto, not some subset.
    /// </summary>
    [Fact]
    public void ScoresMatchBatchMatchingServicePerPair()
    {
        var profile = Profile();
        var records = AuditCliCommon.ReadCsv(CompaniesCsv());

        var rows = records
            .Select(r => (r.SourceRecordId, r.Fields))
            .ToList();
        var batchCsv = BatchMatchingService.BuildMatchesCsv(rows, profile);

        var batchPairs = new Dictionary<(string, string), double>();
        foreach (var line in batchCsv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var parts = line.Trim('\r').Split(',');
            batchPairs[PairKey(parts[0], parts[1])] =
                double.Parse(parts[2], CultureInfo.InvariantCulture);
        }

        Assert.NotEmpty(batchPairs);

        var truth = CorpusAuditCommands.ReadGroundTruth(GroundTruthCsv());
        var audit = new CorpusAuditService(MatchingDefaults.CreateRegistry())
            .Audit(records, profile, truth, maxBlockSize: null, gateMode: true);

        var auditByPair = audit.AllTruePairs.ToDictionary(
            p => PairKey(p.LeftSourceRecordId, p.RightSourceRecordId));

        var compared = 0;
        foreach (var (pair, batchScore) in batchPairs)
        {
            if (!auditByPair.TryGetValue(pair, out var outcome)) continue;   // not a ground-truth pair
            compared++;
            Assert.Equal(CorpusBand.Auto, outcome.Band);
            Assert.Equal(batchScore, outcome.Score!.Value, precision: 9);
        }

        Assert.True(compared > 0, "no batch-emitted pair overlapped ground truth — the comparison proved nothing");

        // Every true pair the audit called Auto must also have been emitted by the batch path and
        // compared above. If these diverge, one path reaches an auto edge the other does not.
        Assert.Equal(audit.Counts.DirectAutoTruePairs, compared);
        Assert.Equal(63, compared);
    }
}
