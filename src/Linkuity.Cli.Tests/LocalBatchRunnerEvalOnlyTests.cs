namespace Linkuity.Cli.Tests;

/// <summary>
/// `--eval-only` / `--fit-fraction`: the CLI-side half of clearing the "a threshold sweep would
/// run over ALL records, including the fit half" blocker. `AuditCliCommon.ApplyEvalOnlyFilter`
/// filters a loaded record set down to the eval half using the EXACT SAME
/// <see cref="FieldEvidenceCalibrationService.IsFitHalf"/> function `match corpus calibrate` uses
/// to build its fit half — never a second, independently written split. This fixture reuses the
/// exact record ids from <c>LocalBatchRunnerCalibrationTests</c> (r5/r6/r9/r10 hash to the FIT
/// half, e1/e2 hash to the EVAL half, at the default fitFraction 0.5) specifically so a single
/// test can tie `match corpus calibrate`'s reported eval count directly to what
/// `match corpus audit --eval-only` and `match corpus ablate --eval-only` actually load — proof
/// the two tools agree, not just that each one individually claims to filter something.
/// </summary>
public class LocalBatchRunnerEvalOnlyTests
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

    private sealed record Fixture(string Dir, string Csv, string GroundTruth, string Profile, string CorpusSource);

    // Same shape as LocalBatchRunnerCalibrationTests' fixture: "block" is Matchable+Blocking, so
    // r5/r6/r9/r10 (all "Smith") form one block and are candidates of each other; e1/e2 also carry
    // "block"="Smith" so they too would be candidates of the fit-half records if --eval-only did
    // NOT actually restrict the record set fed to the audit/ablation services.
    private static Fixture WriteFixture()
    {
        var dir = Path.Combine(Path.GetTempPath(), "linkuity-evalonly-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var csv = Path.Combine(dir, "records.csv");
        File.WriteAllText(csv,
            "\"id\",\"block\",\"a\"\n" +
            "\"r5\",\"Smith\",\"X\"\n" +
            "\"r6\",\"Smith\",\"X\"\n" +
            "\"r9\",\"Smith\",\"Y\"\n" +
            "\"r10\",\"Smith\",\"Y\"\n" +
            "\"e1\",\"Smith\",\"Z1\"\n" +
            "\"e2\",\"Smith\",\"Z2\"\n");

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

        var corpusSource = Path.Combine(dir, "manifest.json");
        File.WriteAllText(corpusSource, "{ \"note\": \"placeholder corpus source for gate-mode hashing\" }");

        return new Fixture(dir, csv, gt, profile, corpusSource);
    }

    // ---- `match corpus audit --eval-only` ties directly to `match corpus calibrate`'s split ----

    [Fact]
    public async Task CorpusAudit_EvalOnly_LoadsExactlyTheEvalHalf_CalibrateReports()
    {
        var f = WriteFixture();

        var (calibExit, calibOut, _) = await RunAsync(
            ["match", "corpus", "calibrate", "--input", f.Csv, "--ground-truth", f.GroundTruth, "--profile", f.Profile]);
        Assert.Equal(0, calibExit);
        Assert.Contains("fit 4", calibOut, StringComparison.Ordinal);
        Assert.Contains("eval 2", calibOut, StringComparison.Ordinal);

        var (auditExit, auditOut, auditErr) = await RunAsync(
            ["match", "corpus", "audit", "--input", f.Csv, "--ground-truth", f.GroundTruth, "--profile", f.Profile,
             "--eval-only"]);

        Assert.Equal(0, auditExit);
        Assert.Equal("", auditErr);
        // "records 2" -- ONLY e1/e2 were loaded, matching calibrate's "eval 2" above exactly. If
        // this ever diverges (a second, independently written split), this is the assertion that
        // would catch it.
        Assert.Contains("records 2", auditOut, StringComparison.Ordinal);
        // The single eval-half true pair (e1-e2) is what audit sees -- not the 3 true pairs the
        // full corpus carries (r5-r6, r9-r10, e1-e2).
        Assert.Contains("true pairs 1", auditOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorpusAudit_WithoutEvalOnly_LoadsTheWholeCorpus()
    {
        var f = WriteFixture();
        var (exit, output, err) = await RunAsync(
            ["match", "corpus", "audit", "--input", f.Csv, "--ground-truth", f.GroundTruth, "--profile", f.Profile]);

        // This test is about WHICH RECORDS get loaded, and it still asserts that: 6 records, 3 true
        // pairs. The exit code is 1 rather than 0 because the fixture genuinely over-merges -- all
        // six records share the blocking field and score exactly at the auto threshold, so three
        // distinct entities collapse into one cluster of 6 against an oracle of 2. That was always
        // true; it used to exit 0 and go unremarked.
        Assert.Equal(1, exit);
        Assert.Contains("GATE FAILED", err, StringComparison.Ordinal);
        Assert.Contains("records 6", output, StringComparison.Ordinal);
        Assert.Contains("true pairs 3", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorpusAudit_FitFractionWithoutEvalOnly_ExitsTwo_WithMessage()
    {
        var f = WriteFixture();
        var (exit, _, err) = await RunAsync(
            ["match", "corpus", "audit", "--input", f.Csv, "--ground-truth", f.GroundTruth, "--profile", f.Profile,
             "--fit-fraction", "0.5"]);

        Assert.Equal(2, exit);
        Assert.Contains("--fit-fraction", err, StringComparison.Ordinal);
        Assert.Contains("--eval-only", err, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("-0.2")]
    [InlineData("not-a-number")]
    public async Task CorpusAudit_InvalidFitFraction_WithEvalOnly_ExitsTwo(string value)
    {
        var f = WriteFixture();
        var (exit, _, err) = await RunAsync(
            ["match", "corpus", "audit", "--input", f.Csv, "--ground-truth", f.GroundTruth, "--profile", f.Profile,
             "--eval-only", "--fit-fraction", value]);

        Assert.Equal(2, exit);
        Assert.Contains("--fit-fraction", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorpusAudit_EvalOnly_WithBaselineGate_ExitsTwo_WithMessage()
    {
        var f = WriteFixture();
        var baselineDir = Path.Combine(f.Dir, "baseline");

        var (exit, _, err) = await RunAsync(
        [
            "match", "corpus", "audit", "--input", f.Csv, "--ground-truth", f.GroundTruth, "--profile", f.Profile,
            "--corpus-source", f.CorpusSource, "--write-baseline", baselineDir, "--eval-only"
        ]);

        Assert.Equal(2, exit);
        Assert.Contains("--eval-only is not supported with the baseline gate", err, StringComparison.Ordinal);
    }

    // ---- `match corpus ablate --eval-only` ----

    [Fact]
    public async Task Ablate_EvalOnly_RestrictsTruePairsToTheEvalHalf()
    {
        var f = WriteFixture();
        var (exit, output, err) = await RunAsync(
        [
            "match", "corpus", "ablate", "--input", f.Csv, "--ground-truth", f.GroundTruth, "--profile", f.Profile,
            "--widths", "onlyblock=block", "--eval-only"
        ]);

        Assert.Equal(0, exit);
        Assert.Equal("", err);
        // "onlyblock " (trailing space) picks only the table row: the "100% precision NOT
        // reachable" diagnostic section (when it fires) also names the width, but followed by
        // ':', never a space.
        var row = output.Split('\n').Single(l => l.TrimStart().StartsWith("onlyblock ", StringComparison.Ordinal));
        var cols = row.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // width, matchable, true pairs, reachability%, ... -- true pairs must be 1 (e1-e2 only),
        // not 3 (the whole corpus).
        Assert.Equal("1", cols[2]);
    }

    [Fact]
    public async Task Ablate_WithoutEvalOnly_SeesTheWholeCorpus()
    {
        var f = WriteFixture();
        var (exit, output, err) = await RunAsync(
        [
            "match", "corpus", "ablate", "--input", f.Csv, "--ground-truth", f.GroundTruth, "--profile", f.Profile,
            "--widths", "onlyblock=block"
        ]);

        Assert.Equal(0, exit);
        Assert.Equal("", err);
        // "onlyblock " (trailing space) picks only the table row: the "100% precision NOT
        // reachable" diagnostic section (when it fires) also names the width, but followed by
        // ':', never a space.
        var row = output.Split('\n').Single(l => l.TrimStart().StartsWith("onlyblock ", StringComparison.Ordinal));
        var cols = row.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("3", cols[2]);
    }

    [Fact]
    public async Task Ablate_FitFractionWithoutEvalOnly_ExitsTwo_WithMessage()
    {
        var f = WriteFixture();
        var (exit, _, err) = await RunAsync(
        [
            "match", "corpus", "ablate", "--input", f.Csv, "--ground-truth", f.GroundTruth, "--profile", f.Profile,
            "--widths", "onlyblock=block", "--fit-fraction", "0.5"
        ]);

        Assert.Equal(2, exit);
        Assert.Contains("--fit-fraction", err, StringComparison.Ordinal);
        Assert.Contains("--eval-only", err, StringComparison.Ordinal);
    }

    // ---- `match scoring audit --eval-only` ----

    [Fact]
    public async Task ScoringAudit_EvalOnly_RestrictsToTheEvalHalf()
    {
        var f = WriteFixture();
        var (exit, output, err) = await RunAsync(
        [
            "match", "scoring", "audit", "--input", f.Csv, "--ground-truth", f.GroundTruth, "--profile", f.Profile,
            "--eval-only"
        ]);

        Assert.Equal(0, exit);
        Assert.Equal("", err);
        Assert.Contains("2 records", output, StringComparison.Ordinal);
        Assert.Contains("1 true pairs", output, StringComparison.Ordinal);
    }
}
