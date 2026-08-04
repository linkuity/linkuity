namespace Linkuity.Cli.Tests;

/// <summary>
/// `match corpus ablate` CLI wiring: flag validation and the happy path end to end. The
/// per-width numbers themselves are pinned in Linkuity.Pipeline.Tests/FieldShapeAblationServiceTests
/// (this fixture reuses the same hand-verified shape: field "b" is Matchable+Blocking, field "d"
/// is Matchable only, and r1/r2 tie under "b" alone but separate cleanly once "d" joins).
/// </summary>
public class LocalBatchRunnerAblationTests
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

    private static Fixture WriteFixture()
    {
        var dir = Path.Combine(Path.GetTempPath(), "linkuity-ablate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var csv = Path.Combine(dir, "records.csv");
        File.WriteAllText(csv,
            "\"id\",\"b\",\"d\"\n" +
            "\"r1\",\"Smith\",\"1\"\n" +
            "\"r2\",\"Smith\",\"1\"\n" +
            "\"r3\",\"Smith\",\"2\"\n");

        var gt = Path.Combine(dir, "ground-truth.csv");
        File.WriteAllText(gt,
            "\"record_id\",\"canonical_key\"\n" +
            "\"r1\",\"G1\"\n\"r2\",\"G1\"\n\"r3\",\"G2\"\n");

        var profile = Path.Combine(dir, "p.profile.json");
        File.WriteAllText(profile, """
        {
          "contentType": "person",
          "fields": [
            { "name": "b", "semanticType": "LastName", "roles": ["Matchable","Blocking"],
              "similarityEvaluator": "exact", "weight": 1.0 },
            { "name": "d", "semanticType": "FirstName", "roles": ["Matchable"],
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

    private const string Widths = "b=b;b+d=b,d";

    // ---- flag validation ----

    [Fact]
    public async Task MissingWidths_ExitsTwo_WithMessage()
    {
        var f = WriteFixture();
        var (exit, _, err) = await RunAsync(
            ["match", "corpus", "ablate", "--input", f.Csv, "--ground-truth", f.GroundTruth, "--profile", f.Profile]);

        Assert.Equal(2, exit);
        Assert.Contains("--widths", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingGroundTruth_ExitsTwo()
    {
        var f = WriteFixture();
        var (exit, _, err) = await RunAsync(
            ["match", "corpus", "ablate", "--input", f.Csv, "--profile", f.Profile, "--widths", Widths]);

        Assert.Equal(2, exit);
        Assert.Contains("Ground-truth", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedWidthsSpec_ExitsTwo_WithMessage()
    {
        var f = WriteFixture();
        var (exit, _, err) = await RunAsync(
        [
            "match", "corpus", "ablate", "--input", f.Csv, "--ground-truth", f.GroundTruth,
            "--profile", f.Profile, "--widths", "not-a-valid-spec"
        ]);

        Assert.Equal(2, exit);
        Assert.Contains("Invalid --widths entry", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WidthNamingAnUnknownField_ExitsTwo()
    {
        var f = WriteFixture();
        var (exit, _, err) = await RunAsync(
        [
            "match", "corpus", "ablate", "--input", f.Csv, "--ground-truth", f.GroundTruth,
            "--profile", f.Profile, "--widths", "bad=nope"
        ]);

        Assert.Equal(2, exit);
        Assert.Contains("nope", err, StringComparison.Ordinal);
    }

    // ---- happy path ----

    [Fact]
    public async Task ReportRun_PrintsPerWidthTable_AndDetectsAThresholdThatMoves()
    {
        var f = WriteFixture();
        var (exit, output, err) = await RunAsync(
        [
            "match", "corpus", "ablate", "--input", f.Csv, "--ground-truth", f.GroundTruth,
            "--profile", f.Profile, "--widths", Widths
        ]);

        Assert.Equal(0, exit);
        Assert.Equal("", err);
        Assert.Contains("=== field-shape ablation ===", output, StringComparison.Ordinal);
        Assert.Contains("b", output, StringComparison.Ordinal);
        Assert.Contains("b+d", output, StringComparison.Ordinal);

        // Width "b" alone never reaches 100% precision (r1-r2 true and r1-r3/r2-r3 false all tie
        // at the same score); width "b+d" reaches it at recall 100%. The report must say so
        // explicitly for "b", not paper over it with the closest precision observed.
        Assert.Contains("unreachable", output, StringComparison.Ordinal);
        Assert.Contains("100% precision is NOT reachable at ANY threshold", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verb_IsDispatchedFromMatchCorpus_BeforeTheGenericAuditHandler()
    {
        // "ablate" must be recognized as its own verb, not swallowed by `match corpus audit`'s
        // dispatch (which requires --ground-truth in a different shape and would fail differently).
        var f = WriteFixture();
        var (exit, output, _) = await RunAsync(
        [
            "match", "corpus", "ablate", "--input", f.Csv, "--ground-truth", f.GroundTruth,
            "--profile", f.Profile, "--widths", Widths
        ]);

        Assert.Equal(0, exit);
        Assert.Contains("field-shape ablation", output, StringComparison.Ordinal);
    }
}
