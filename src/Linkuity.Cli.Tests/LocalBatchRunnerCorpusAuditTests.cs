using System.Reflection;
using System.Text;
using Linkuity.Cli;
using Linkuity.Pipeline;

namespace Linkuity.Cli.Tests;

/// <summary>
/// `match corpus audit` CLI wiring. The gate's exit codes are a contract — 0 pass / report-only,
/// 1 comparable-and-worse, 2 usage error or refusal — and most of these tests exist to keep the
/// five wiring hazards recorded in the plan from regressing.
/// </summary>
public class LocalBatchRunnerCorpusAuditTests
{
    private static async Task<(int Exit, string Out, string Err)> RunAsync(string[] args)
    {
        using var outW = new StringWriter();
        using var errW = new StringWriter();
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        Console.SetOut(outW);
        Console.SetError(errW);
        try
        {
            var exit = await new LocalBatchRunner().RunAsync(args, CancellationToken.None);
            return (exit, outW.ToString(), errW.ToString());
        }
        finally
        {
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
        }
    }

    private sealed record Fixture(string Dir, string Csv, string GroundTruth, string ProfileA, string ProfileB)
    {
        public string BaselineDir => Path.Combine(Dir, "baseline");
        public string StrataPath => Path.Combine(BaselineDir, CorpusAuditBaseline.StrataFileName);
        public string JsonPath => Path.Combine(BaselineDir, CorpusAuditBaseline.BaselineFileName);
    }

    /// <summary>
    /// Four records, two true pairs, engineered so a PROFILE change alone reclassifies one pair
    /// between strata while every evaluation input (records, ground truth) stays byte-identical.
    /// <para>
    /// Profile A scores and blocks on organization_name. Profile B blocks on organization_name
    /// (so reachability is unchanged and cannot confound the gate) but scores on `alias`, which
    /// is also the first matchable OrganizationName field and therefore the one the strata are
    /// computed from.
    /// </para>
    /// <list type="bullet">
    /// <item><description>p1/p2 — identical under both fields: S1 under A, S1 under B, auto-matched by both.</description></item>
    /// <item><description>q1/q2 — identical organization_name but disjoint aliases: S1 under A,
    /// S5 under B, auto-matched by A and rejected by B.</description></item>
    /// </list>
    /// So under B the q pair both MOVES stratum and STOPS matching — which is what separates the
    /// frozen bucketing (S1 2/2 -> 1/2, a failure) from the current-run bucketing (S1 1/1, healthy).
    /// </summary>
    private static Fixture WriteFixture()
    {
        var dir = Path.Combine(Path.GetTempPath(), "linkuity-corpus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var csv = Path.Combine(dir, "companies.csv");
        File.WriteAllText(csv,
            "\"id\",\"organization_name\",\"alias\"\n" +
            "\"p1\",\"APEX ENERGY LLC\",\"APEX ENERGY LLC\"\n" +
            "\"p2\",\"APEX ENERGY LLC\",\"APEX ENERGY LLC\"\n" +
            "\"q1\",\"BOREAL MINING CORP\",\"ZEPHYR HOLDINGS\"\n" +
            "\"q2\",\"BOREAL MINING CORP\",\"OMEGA VENTURES\"\n");

        var gt = Path.Combine(dir, "ground-truth.csv");
        File.WriteAllText(gt,
            "\"record_id\",\"canonical_key\"\n" +
            "\"p1\",\"apex\"\n\"p2\",\"apex\"\n\"q1\",\"boreal\"\n\"q2\",\"boreal\"\n");

        var profileA = Path.Combine(dir, "a.profile.json");
        File.WriteAllText(profileA, """
        {
          "contentType": "organization",
          "fields": [
            { "name": "organization_name", "semanticType": "OrganizationName",
              "roles": ["Searchable","Matchable","Blocking"],
              "similarityEvaluator": "canonical-jaccard", "weight": 4.0 }
          ],
          "normalizationStrategy": "identity",
          "maxBlockSize": 50,
          "blockingStrategies": ["exact-value","token"],
          "candidateRetrievalStrategy": "linear",
          "similarityStrategy": "field-weighted",
          "scoringStrategy": "identifier-weighted",
          "decisionStrategy": "threshold",
          "clusteringStrategy": "union-find",
          "autoMatchThreshold": 0.41,
          "reviewThreshold": 0.31
        }
        """);

        var profileB = Path.Combine(dir, "b.profile.json");
        File.WriteAllText(profileB, """
        {
          "contentType": "organization",
          "fields": [
            { "name": "alias", "semanticType": "OrganizationName", "roles": ["Matchable"],
              "similarityEvaluator": "canonical-jaccard", "weight": 4.0 },
            { "name": "organization_name", "semanticType": "OrganizationName",
              "roles": ["Searchable","Blocking"] }
          ],
          "normalizationStrategy": "identity",
          "maxBlockSize": 50,
          "blockingStrategies": ["exact-value","token"],
          "candidateRetrievalStrategy": "linear",
          "similarityStrategy": "field-weighted",
          "scoringStrategy": "identifier-weighted",
          "decisionStrategy": "threshold",
          "clusteringStrategy": "union-find",
          "autoMatchThreshold": 0.41,
          "reviewThreshold": 0.31
        }
        """);

        return new Fixture(dir, csv, gt, profileA, profileB);
    }

    private static string[] Audit(Fixture f, string profile, params string[] extra) =>
    [
        "match", "corpus", "audit",
        "--input", f.Csv, "--ground-truth", f.GroundTruth, "--profile", profile,
        .. extra
    ];

    private static async Task<Fixture> WriteBaselineAsync()
    {
        var f = WriteFixture();
        var (exit, _, err) = await RunAsync(Audit(f, f.ProfileA, "--write-baseline", f.BaselineDir));
        Assert.Equal(0, exit);
        Assert.Equal("", err);
        return f;
    }

    // ---- report-only ----

    [Fact]
    public async Task ReportOnlyRun_PrintsReportAndExitsZero()
    {
        var f = WriteFixture();

        var (exit, output, err) = await RunAsync(Audit(f, f.ProfileA));

        Assert.Equal(0, exit);
        Assert.Equal("", err);
        Assert.Contains("=== corpus audit ===", output, StringComparison.Ordinal);
        Assert.Contains("records 4   unlabeled 0   true pairs 2", output, StringComparison.Ordinal);
        Assert.Contains("post-cluster pairwise recall", output, StringComparison.Ordinal);
        Assert.Contains("S1Identical", output, StringComparison.Ordinal);
    }

    /// <summary>The single-field note is driven by the coverage data, not hardcoded: this corpus
    /// populates exactly one matchable field, so the note names THAT field.</summary>
    [Fact]
    public async Task SingleFieldNote_NamesTheFieldFromCoverageData()
    {
        var f = WriteFixture();

        var (_, outputA, _) = await RunAsync(Audit(f, f.ProfileA));
        var (_, outputB, _) = await RunAsync(Audit(f, f.ProfileB));

        Assert.Contains("NOTE: organization_name is the only field populated", outputA, StringComparison.Ordinal);
        Assert.Contains("NOTE: alias is the only field populated", outputB, StringComparison.Ordinal);
    }

    /// <summary>FloorLiftedPairs counts any floor (the 0.98 identifier floor as well as the review
    /// floor), so the report must not claim the review floor specifically.</summary>
    [Fact]
    public async Task FloorLiftLine_IsNotLabelledReviewFloorSpecific()
    {
        var f = WriteFixture();

        var (_, output, _) = await RunAsync(Audit(f, f.ProfileA));

        Assert.Contains("lifted by a floor", output, StringComparison.Ordinal);
        Assert.DoesNotContain("lifted by the review floor", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CsvFormat_EmitsSectionKeyValueRows()
    {
        var f = WriteFixture();

        var (exit, output, _) = await RunAsync(Audit(f, f.ProfileA, "--format", "csv"));

        Assert.Equal(0, exit);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r')).ToList();
        Assert.Equal("section,key,value", lines[0]);
        Assert.Contains("count,records,4", lines);
        Assert.Contains("count,true_pairs,2", lines);
        Assert.Contains("stratum,S1Identical.true_pairs,2", lines);
        Assert.Contains("metric,cluster_pairwise_precision,1.000000", lines);
    }

    // ---- usage / validation (exit 2) ----

    [Fact]
    public async Task UnknownVerb_Exit2()
    {
        var f = WriteFixture();

        var (exit, _, err) = await RunAsync(
            ["match", "corpus", "frobnicate", "--input", f.Csv, "--profile", f.ProfileA]);

        Assert.Equal(2, exit);
        Assert.Contains("match corpus audit", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingProfile_Exit2()
    {
        var f = WriteFixture();

        var (exit, _, err) = await RunAsync(
            ["match", "corpus", "audit", "--input", f.Csv, "--ground-truth", f.GroundTruth]);

        Assert.Equal(2, exit);
        Assert.Contains("--profile", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingGroundTruth_Exit2()
    {
        var f = WriteFixture();

        var (exit, _, err) = await RunAsync(
            ["match", "corpus", "audit", "--input", f.Csv, "--profile", f.ProfileA]);

        Assert.Equal(2, exit);
        Assert.Contains("Ground-truth", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAndCompareTogether_Exit2()
    {
        var f = WriteFixture();

        var (exit, _, err) = await RunAsync(Audit(f, f.ProfileA,
            "--write-baseline", f.BaselineDir, "--compare-baseline", f.BaselineDir));

        Assert.Equal(2, exit);
        Assert.Contains("not both", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidTop_Exit2()
    {
        var f = WriteFixture();

        var (exit, _, err) = await RunAsync(Audit(f, f.ProfileA, "--top", "0"));

        Assert.Equal(2, exit);
        Assert.Contains("--top", err, StringComparison.Ordinal);
    }

    /// <summary>Gate mode demands exact record/ground-truth ID-set equality; the service raises
    /// that as a validation error, which the CLI surfaces as exit 2 rather than a gate verdict.</summary>
    [Fact]
    public async Task GateModeWithUnlabeledRecord_Exit2()
    {
        var f = WriteFixture();
        File.WriteAllText(f.GroundTruth,
            "\"record_id\",\"canonical_key\"\n" +
            "\"p1\",\"apex\"\n\"p2\",\"apex\"\n\"q1\",\"boreal\"\n");

        var (exit, _, err) = await RunAsync(Audit(f, f.ProfileA, "--write-baseline", f.BaselineDir));

        Assert.Equal(2, exit);
        Assert.Contains("Gate mode requires every record to be labeled", err, StringComparison.Ordinal);
        Assert.False(File.Exists(f.JsonPath));
    }

    [Fact]
    public async Task DuplicateGroundTruthId_Exit2()
    {
        var f = WriteFixture();
        File.WriteAllText(f.GroundTruth,
            "\"record_id\",\"canonical_key\"\n\"p1\",\"apex\"\n\"p1\",\"apex\"\n");

        var (exit, _, err) = await RunAsync(Audit(f, f.ProfileA));

        Assert.Equal(2, exit);
        Assert.Contains("p1", err, StringComparison.Ordinal);
    }

    // ---- --write-baseline ----

    [Fact]
    public async Task WriteBaseline_EmitsBothArtifactsAndExitsZero()
    {
        var f = await WriteBaselineAsync();

        Assert.True(File.Exists(f.JsonPath));
        Assert.True(File.Exists(f.StrataPath));
        Assert.StartsWith(CorpusAuditBaseline.StrataHeader, File.ReadAllText(f.StrataPath), StringComparison.Ordinal);
    }

    /// <summary>The corpus directory is not version-controlled, so "written once" has to be
    /// enforced by the tool. WriteAtomic's explanation is surfaced intact, not paraphrased.</summary>
    [Fact]
    public async Task WriteBaseline_OverExistingBaseline_Exit2WithWriteAtomicMessage()
    {
        var f = await WriteBaselineAsync();

        var (exit, _, err) = await RunAsync(Audit(f, f.ProfileA, "--write-baseline", f.BaselineDir));

        Assert.Equal(2, exit);
        Assert.Contains("A baseline already exists", err, StringComparison.Ordinal);
        Assert.Contains("--replace-baseline", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteBaseline_WithReplace_Succeeds()
    {
        var f = await WriteBaselineAsync();

        var (exit, output, err) = await RunAsync(Audit(f, f.ProfileA,
            "--write-baseline", f.BaselineDir, "--replace-baseline"));

        Assert.Equal(0, exit);
        Assert.Equal("", err);
        Assert.Contains("Baseline written to", output, StringComparison.Ordinal);
    }

    // ---- --compare-baseline ----

    [Fact]
    public async Task CompareBaseline_UnchangedRun_PassesWithReportBeforeVerdict()
    {
        var f = await WriteBaselineAsync();

        var (exit, output, err) = await RunAsync(Audit(f, f.ProfileA, "--compare-baseline", f.BaselineDir));

        Assert.Equal(0, exit);
        Assert.Equal("", err);
        Assert.Contains("GATE PASSED.", output, StringComparison.Ordinal);
        // A verdict with no visible numbers is not actionable: the report precedes it.
        Assert.True(
            output.IndexOf("=== corpus audit ===", StringComparison.Ordinal)
            < output.IndexOf("GATE PASSED.", StringComparison.Ordinal));
        // Nothing moved, so no warning is printed.
        Assert.DoesNotContain("changed stratum", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompareBaseline_IncompleteDirectory_Exit2()
    {
        var f = WriteFixture();
        Directory.CreateDirectory(f.BaselineDir);

        var (exit, _, err) = await RunAsync(Audit(f, f.ProfileA, "--compare-baseline", f.BaselineDir));

        Assert.Equal(2, exit);
        Assert.Contains("Baseline incomplete", err, StringComparison.Ordinal);
    }

    // ---- HAZARD 1: compare must bucket by the FROZEN assignment, never by the current run ----

    /// <summary>
    /// The regression spec §8.1 exists to prevent. Under profile B the q pair moves S1 -> S5 AND
    /// stops matching. Bucketed by the BASELINE's frozen assignment, S1 goes 2/2 -> 1/2 and the
    /// gate FAILS. Bucketed by the current run (i.e. wired through <c>Create</c>), S1 would read
    /// 1/1 = healthy and S5 would be skipped because the baseline cohort is empty — the run would
    /// PASS. Reachability and precision are held constant by the fixture so this failure can only
    /// come from the per-stratum rule.
    /// </summary>
    [Fact]
    public async Task CompareBaseline_PairMovedOutOfItsStratum_StillFailsInItsFrozenCohort()
    {
        var f = await WriteBaselineAsync();

        var (exit, output, err) = await RunAsync(Audit(f, f.ProfileB,
            "--compare-baseline", f.BaselineDir, "--accept-profile-change"));

        Assert.Equal(1, exit);
        Assert.Contains("GATE FAILED:", err, StringComparison.Ordinal);
        // "2/2 -> 1/2" is the frozen cohort. Bucketing by the current run gives "1/1".
        Assert.Contains("S1Identical post-cluster recall fell beyond the 0.5pp tolerance (2/2 -> 1/2)",
            err, StringComparison.Ordinal);
        Assert.Contains("stop and escalate", err, StringComparison.Ordinal);
        // The current run does classify the moved pair into S5 — the report shows that, and the
        // gated cohort above is nonetheless the baseline's.
        Assert.Contains("S5Disjoint", output, StringComparison.Ordinal);
    }

    // ---- HAZARD 2: strataSha256 is the sidecar's identity, not the current run's classification ----

    /// <summary>
    /// §8.1 reports reclassification rather than gating it, so the current run's strata are
    /// EXPECTED to differ. Had the CLI set the current run's StrataSha256 from its own recomputed
    /// strata, this run — where one pair genuinely moved — would have been REFUSED (exit 2) and
    /// the real regression never reported. It must be a comparable FAILURE (exit 1).
    /// </summary>
    [Fact]
    public async Task CompareBaseline_ReclassifiedRun_IsFailedNotRefused()
    {
        var f = await WriteBaselineAsync();

        var (exit, _, err) = await RunAsync(Audit(f, f.ProfileB,
            "--compare-baseline", f.BaselineDir, "--accept-profile-change"));

        Assert.Equal(1, exit);
        Assert.DoesNotContain("GATE REFUSED", err, StringComparison.Ordinal);
        Assert.DoesNotContain("strataSha256", err, StringComparison.Ordinal);
    }

    // ---- HAZARD 3: the sidecar is hashed as TEXT read from disk, not as raw bytes ----

    /// <summary>
    /// Re-encoding the sidecar with a UTF-8 BOM changes its bytes but not its text.
    /// <c>File.ReadAllTextAsync</c> strips the BOM and <c>Sha256Of</c> re-encodes the string, so
    /// the integrity check still passes. Hashing raw file bytes would refuse this run.
    /// </summary>
    [Fact]
    public async Task CompareBaseline_SidecarReEncodedWithBom_StillVerifies()
    {
        var f = await WriteBaselineAsync();
        var text = File.ReadAllText(f.StrataPath);
        File.WriteAllText(f.StrataPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal(0xEF, File.ReadAllBytes(f.StrataPath)[0]);

        var (exit, output, err) = await RunAsync(Audit(f, f.ProfileA, "--compare-baseline", f.BaselineDir));

        Assert.Equal(0, exit);
        Assert.Equal("", err);
        Assert.Contains("GATE PASSED.", output, StringComparison.Ordinal);
    }

    // ---- HAZARD 4: the hash-verifying overload is the only route in ----

    /// <summary>
    /// An edited sidecar is caught, which is only possible if the CLI took the two-arg overload.
    /// </summary>
    [Fact]
    public async Task CompareBaseline_EditedSidecar_Exit2WithIntegrityMessage()
    {
        var f = await WriteBaselineAsync();
        // One pair quietly reassigned out of the gated cohort into excluded S5.
        File.WriteAllText(f.StrataPath,
            File.ReadAllText(f.StrataPath).Replace("q1,q2,S1Identical", "q1,q2,S5Disjoint", StringComparison.Ordinal));

        var (exit, _, err) = await RunAsync(Audit(f, f.ProfileA, "--compare-baseline", f.BaselineDir));

        Assert.Equal(2, exit);
        Assert.Contains("Frozen strata sidecar does not match the baseline it accompanies",
            err, StringComparison.Ordinal);
    }

    /// <summary>
    /// The structural half of the same guard: the parse-only overload must not be publicly
    /// reachable, or a future caller can skip the integrity check by reaching for the shorter
    /// signature. This assembly has no InternalsVisibleTo from Linkuity.Pipeline, so it sees
    /// exactly the surface the CLI does.
    /// </summary>
    [Fact]
    public void ParseOnlyReadStrataCsvOverloadIsNotPubliclyReachable()
    {
        var type = typeof(CorpusAuditBaseline);

        Assert.Null(type.GetMethod("ReadStrataCsv", BindingFlags.Public | BindingFlags.Static,
            binder: null, types: [typeof(string)], modifiers: null));
        Assert.NotNull(type.GetMethod("ReadStrataCsv", BindingFlags.Public | BindingFlags.Static,
            binder: null, types: [typeof(string), typeof(string)], modifiers: null));
    }

    // ---- HAZARD 5: reclassifiedPairs comes from CountReclassified, never a literal 0 ----

    /// <summary>
    /// The compiler forces a value for <c>reclassifiedPairs</c> but cannot stop a caller passing
    /// <c>0</c>. One pair genuinely moves here, so the reported count must be 1.
    /// </summary>
    [Fact]
    public async Task CompareBaseline_ReportsNonZeroReclassificationWhenStrataMoved()
    {
        var f = await WriteBaselineAsync();

        var (_, output, _) = await RunAsync(Audit(f, f.ProfileB,
            "--compare-baseline", f.BaselineDir, "--accept-profile-change"));

        Assert.Contains("WARNING: 1 true pair(s) changed stratum since the baseline",
            output, StringComparison.Ordinal);
        Assert.Contains("bucketed by the BASELINE assignment", output, StringComparison.Ordinal);
    }

    // ---- spec §10 amendment: evaluation inputs always refuse, system-under-test can be accepted ----

    [Fact]
    public async Task CompareBaseline_ProfileChangedWithoutAcceptFlag_RefusedExit2()
    {
        var f = await WriteBaselineAsync();

        var (exit, _, err) = await RunAsync(Audit(f, f.ProfileB, "--compare-baseline", f.BaselineDir));

        Assert.Equal(2, exit);
        Assert.Contains("GATE REFUSED.", err, StringComparison.Ordinal);
        Assert.Contains("profileSha256 changed", err, StringComparison.Ordinal);
        // A refusal never carries failures: it must not be mistaken for a regression.
        Assert.DoesNotContain("GATE FAILED", err, StringComparison.Ordinal);
    }

    /// <summary>The amendment lifts the profile refusal only. Ground truth is an EVALUATION
    /// input and always refuses, --accept-profile-change or not.</summary>
    [Fact]
    public async Task CompareBaseline_GroundTruthChanged_RefusedEvenWithAcceptFlag()
    {
        var f = await WriteBaselineAsync();
        // Same grouping, different label strings: the true-pair set is unchanged, so only the
        // ground-truth hash can be what refuses this.
        File.WriteAllText(f.GroundTruth,
            "\"record_id\",\"canonical_key\"\n" +
            "\"p1\",\"apex-2\"\n\"p2\",\"apex-2\"\n\"q1\",\"boreal-2\"\n\"q2\",\"boreal-2\"\n");

        var (exit, _, err) = await RunAsync(Audit(f, f.ProfileA,
            "--compare-baseline", f.BaselineDir, "--accept-profile-change"));

        Assert.Equal(2, exit);
        Assert.Contains("groundTruthSha256 changed", err, StringComparison.Ordinal);
    }

    /// <summary>Records are an EVALUATION input too. Adding a record changes the true-pair set,
    /// so the run is not comparable at all — exit 2, never exit 1.</summary>
    [Fact]
    public async Task CompareBaseline_RecordsChanged_RefusedEvenWithAcceptFlag()
    {
        var f = await WriteBaselineAsync();
        File.AppendAllText(f.Csv, "\"p3\",\"APEX ENERGY LLC\",\"APEX ENERGY LLC\"\n");
        File.AppendAllText(f.GroundTruth, "\"p3\",\"apex\"\n");

        var (exit, _, err) = await RunAsync(Audit(f, f.ProfileA,
            "--compare-baseline", f.BaselineDir, "--accept-profile-change"));

        Assert.Equal(2, exit);
        Assert.Contains("recordsSha256 changed", err, StringComparison.Ordinal);
    }
}
