namespace Linkuity.Cli.Tests;

/// <summary>
/// `match corpus calibrate` CLI wiring: flag validation and the happy path end to end. The
/// numbers themselves are pinned in
/// Linkuity.Pipeline.Tests/FieldEvidenceCalibrationServiceTests (this fixture reuses the exact
/// same hand-verified shape: field "block" is Matchable+Blocking and is its own excluded origin,
/// "a" is SMOOTHING-DEPENDENT, "b" is UNUSABLE, "c" has no same-entity data at all) so the happy
/// path below can assert on the formatter's rendering of every guard-rail section — UNUSABLE, NO
/// ESTIMATE, SMOOTHING-DEPENDENT, and the per-origin determination table — in one run, the same
/// way <c>LocalBatchRunnerAblationTests</c> exercises its own formatter's sections through the
/// CLI rather than a separate formatter-only test file.
/// </summary>
public class LocalBatchRunnerCalibrationTests
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

    private sealed record Fixture(string Dir, string Csv, string GroundTruth, string Profile);

    // Same shape as FieldEvidenceCalibrationServiceTests' block/a/b/c fixture: "block" is the
    // sole blocking field (Matchable too), so its own origin determines itself completely (NO
    // ESTIMATE for both m and u); "a" hits both raw 0/1 boundaries (SMOOTHING-DEPENDENT); "b" has
    // m == u (UNUSABLE); "c" has zero same-entity observations (NO ESTIMATE for m only). e1/e2
    // hash to the EVAL half at the default fitFraction 0.5 and are never candidates.
    private static Fixture WriteFixture()
    {
        var dir = Path.Combine(Path.GetTempPath(), "linkuity-calibrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var csv = Path.Combine(dir, "records.csv");
        File.WriteAllText(csv,
            "\"id\",\"block\",\"a\",\"b\",\"c\"\n" +
            "\"r5\",\"Smith\",\"X\",\"P\",\"V1\"\n" +
            "\"r6\",\"Smith\",\"X\",\"Q\",\"\"\n" +
            "\"r9\",\"Smith\",\"Y\",\"P\",\"\"\n" +
            "\"r10\",\"Smith\",\"Y\",\"P\",\"V1\"\n" +
            "\"e1\",\"Smith\",\"Z1\",\"\",\"\"\n" +
            "\"e2\",\"Smith\",\"Z2\",\"\",\"\"\n");

        var gt = Path.Combine(dir, "ground-truth.csv");
        File.WriteAllText(gt,
            "\"record_id\",\"canonical_key\"\n" +
            "\"r5\",\"G1\"\n\"r6\",\"G1\"\n\"r9\",\"G2\"\n\"r10\",\"G2\"\n" +
            "\"e1\",\"G3\"\n\"e2\",\"G3\"\n");

        var profile = Path.Combine(dir, "p.profile.json");
        File.WriteAllText(profile, """
        {
          "contentType": "person",
          "fields": [
            { "name": "block", "semanticType": "LastName", "roles": ["Matchable","Blocking"],
              "similarityEvaluator": "exact", "weight": 1.0 },
            { "name": "a", "semanticType": "FirstName", "roles": ["Matchable"],
              "similarityEvaluator": "exact", "weight": 1.0 },
            { "name": "b", "semanticType": "FirstName", "roles": ["Matchable"],
              "similarityEvaluator": "exact", "weight": 1.0 },
            { "name": "c", "semanticType": "FirstName", "roles": ["Matchable"],
              "similarityEvaluator": "exact", "weight": 1.0 }
          ],
          "normalizationStrategy": "identity",
          "blockingStrategies": ["token-name"],
          "candidateRetrievalStrategy": "linear",
          "similarityStrategy": "field-weighted",
          "scoringStrategy": "weighted",
          "decisionStrategy": "threshold",
          "clusteringStrategy": "union-find",
          "autoMatchThreshold": 0.5,
          "reviewThreshold": 0.3
        }
        """);

        return new Fixture(dir, csv, gt, profile);
    }

    // ---- flag validation ----

    [Fact]
    public async Task MissingProfile_ExitsTwo()
    {
        var f = WriteFixture();
        var (exit, _, err) = await RunAsync(
            ["match", "corpus", "calibrate", "--input", f.Csv, "--ground-truth", f.GroundTruth]);

        Assert.Equal(2, exit);
        Assert.Contains("Usage: match corpus calibrate", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingGroundTruth_ExitsTwo_WithMessage()
    {
        var f = WriteFixture();
        var (exit, _, err) = await RunAsync(
            ["match", "corpus", "calibrate", "--input", f.Csv, "--profile", f.Profile]);

        Assert.Equal(2, exit);
        Assert.Contains("Ground-truth", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GroundTruthFileMissing_ExitsTwo()
    {
        var f = WriteFixture();
        var (exit, _, err) = await RunAsync(
        [
            "match", "corpus", "calibrate", "--input", f.Csv, "--profile", f.Profile,
            "--ground-truth", Path.Combine(f.Dir, "does-not-exist.csv")
        ]);

        Assert.Equal(2, exit);
        Assert.Contains("Ground-truth", err, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("-0.2")]
    [InlineData("1.5")]
    [InlineData("not-a-number")]
    public async Task InvalidFitFraction_ExitsTwo_WithMessage(string value)
    {
        var f = WriteFixture();
        var (exit, _, err) = await RunAsync(
        [
            "match", "corpus", "calibrate", "--input", f.Csv, "--ground-truth", f.GroundTruth,
            "--profile", f.Profile, "--fit-fraction", value
        ]);

        Assert.Equal(2, exit);
        Assert.Contains("--fit-fraction", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InputCsvMissing_ExitsTwo()
    {
        var f = WriteFixture();
        var (exit, _, err) = await RunAsync(
        [
            "match", "corpus", "calibrate", "--input", Path.Combine(f.Dir, "nope.csv"),
            "--ground-truth", f.GroundTruth, "--profile", f.Profile
        ]);

        Assert.Equal(2, exit);
        Assert.NotEqual("", err);
    }

    [Fact]
    public async Task NonIdentityNormalization_ExitsTwo_ViaServiceValidation()
    {
        var f = WriteFixture();
        var badProfile = Path.Combine(f.Dir, "bad.profile.json");
        File.WriteAllText(badProfile, File.ReadAllText(f.Profile).Replace("\"identity\"", "\"phone-normalize\""));

        var (exit, _, err) = await RunAsync(
        [
            "match", "corpus", "calibrate", "--input", f.Csv, "--ground-truth", f.GroundTruth,
            "--profile", badProfile
        ]);

        Assert.Equal(2, exit);
        Assert.Contains("identity", err, StringComparison.Ordinal);
    }

    // ---- happy path: exercises every formatter guard-rail section in one run ----

    [Fact]
    public async Task ReportRun_PrintsEveryGuardRailSection()
    {
        var f = WriteFixture();
        var (exit, output, err) = await RunAsync(
        [
            "match", "corpus", "calibrate", "--input", f.Csv, "--ground-truth", f.GroundTruth,
            "--profile", f.Profile
        ]);

        Assert.Equal(0, exit);
        Assert.Equal("", err);
        Assert.Contains("=== field evidence calibration ===", output, StringComparison.Ordinal);

        // Split/candidate bookkeeping: 4 fit (r5,r6,r9,r10), 2 eval (e1,e2), matching the
        // pipeline-level fixture this reuses.
        Assert.Contains("fit 4", output, StringComparison.Ordinal);
        Assert.Contains("eval 2", output, StringComparison.Ordinal);

        // "b": m == u -> UNUSABLE section renders, with the "DECREASES" consequence spelled out.
        Assert.Contains("UNUSABLE", output, StringComparison.Ordinal);
        Assert.Contains("DECREASES AS SIMILARITY INCREASES", output, StringComparison.Ordinal);

        // "block" and "c": NO ESTIMATE section renders for at least one field.
        Assert.Contains("NO ESTIMATE", output, StringComparison.Ordinal);

        // "a": both raw boundaries hit -> SMOOTHING-DEPENDENT section renders, with both
        // constants shown.
        Assert.Contains("SMOOTHING-DEPENDENT", output, StringComparison.Ordinal);
        Assert.Contains("alpha= 0.5", output, StringComparison.Ordinal);
        Assert.Contains("alpha= 1.0", output, StringComparison.Ordinal);

        // Per-origin determination table: "block" is the sole origin for every field, and it is
        // reported as the field's own (excluded) origin at least once.
        Assert.Contains("PER-ORIGIN DETERMINATION", output, StringComparison.Ordinal);
        Assert.Contains("origin=block", output, StringComparison.Ordinal);
        Assert.Contains("EXCLUDED", output, StringComparison.Ordinal);

        // Conditioned-vs-unconditioned m section renders for "block", whose own origin was
        // excluded from both m and u.
        Assert.Contains("CONDITIONED VS UNCONDITIONED m", output, StringComparison.Ordinal);

        Assert.Contains("similarity distribution", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verb_IsDispatchedFromMatchCorpus_BeforeTheGenericAuditHandler()
    {
        // "calibrate" must be recognized as its own verb, not swallowed by `match corpus audit`'s
        // dispatch (which requires different flags and would fail differently).
        var f = WriteFixture();
        var (exit, output, _) = await RunAsync(
        [
            "match", "corpus", "calibrate", "--input", f.Csv, "--ground-truth", f.GroundTruth,
            "--profile", f.Profile
        ]);

        Assert.Equal(0, exit);
        Assert.Contains("field evidence calibration", output, StringComparison.Ordinal);
    }
}
