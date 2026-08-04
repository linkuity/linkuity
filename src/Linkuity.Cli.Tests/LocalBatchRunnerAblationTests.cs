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

    // ---- evidence scorer (log-odds scale) ----

    /// <summary>
    /// Same records/truth as WriteFixture, but scored with 'evidence' instead of 'weighted':
    /// each of "b"/"d" carries FieldEvidence (sameEntityAgreement 0.9, chanceAgreement 0.1,
    /// capped at 6 bits — well under the 8.0 auto threshold, as the config loader requires for a
    /// non-identifier field). Hand-verified: log2(0.9/0.1) ~= 3.169925 bits per agreeing field, so
    /// r1-r2 (both fields agree) totals ~6.339850 and r1-r3/r2-r3 (b agrees, d disagrees, and
    /// disagreement is the same magnitude negated) total ~0 -- a clean separation reachable at
    /// 100% precision, recall 100%, at cut ~6.3399: comfortably outside [0,1], which is exactly
    /// what an evidence-scored profile is supposed to look like and a UnitInterval-only harness
    /// could never accept.
    /// </summary>
    private static string WriteEvidenceProfile(string dir)
    {
        var profile = Path.Combine(dir, "evidence.profile.json");
        File.WriteAllText(profile, """
        {
          "contentType": "person",
          "fields": [
            { "name": "b", "semanticType": "LastName", "roles": ["Matchable","Blocking"],
              "similarityEvaluator": "exact", "weight": 1.0,
              "evidence": { "sameEntityAgreement": 0.9, "chanceAgreement": 0.1, "maxAgreementBits": 6.0 } },
            { "name": "d", "semanticType": "FirstName", "roles": ["Matchable"],
              "similarityEvaluator": "exact", "weight": 1.0,
              "evidence": { "sameEntityAgreement": 0.9, "chanceAgreement": 0.1, "maxAgreementBits": 6.0 } }
          ],
          "normalizationStrategy": "identity",
          "blockingStrategies": ["token-name"],
          "candidateRetrievalStrategy": "linear",
          "similarityStrategy": "field-weighted",
          "scoringStrategy": "evidence",
          "decisionStrategy": "threshold",
          "clusteringStrategy": "union-find",
          "autoMatchThreshold": 8.0,
          "reviewThreshold": 4.0
        }
        """);
        return profile;
    }

    [Fact]
    public async Task EvidenceScoredProfile_RunsToCompletion_OnLogOddsThresholds_RatherThanBeingRefused()
    {
        var f = WriteFixture();
        var evidenceProfile = WriteEvidenceProfile(f.Dir);

        var (exit, output, err) = await RunAsync(
        [
            "match", "corpus", "ablate", "--input", f.Csv, "--ground-truth", f.GroundTruth,
            "--profile", evidenceProfile, "--widths", "full=b,d"
        ]);

        Assert.Equal(0, exit);
        Assert.Equal("", err);
        Assert.DoesNotContain("requires scoringStrategy", output, StringComparison.Ordinal);
        Assert.Contains("scoring strategy: evidence", output, StringComparison.Ordinal);
        // The 100%-precision threshold sits at ~6.34 bits -- far outside [0,1], proof this is
        // read on the evidence scorer's own LogOdds scale rather than being clamped or refused.
        Assert.Contains("6.3399", output, StringComparison.Ordinal);
        Assert.Contains("100.0 %", output, StringComparison.Ordinal); // recall at that cut
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
