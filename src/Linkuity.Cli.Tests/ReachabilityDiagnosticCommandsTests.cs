using System.Text.Json;

namespace Linkuity.Cli.Tests;

/// <summary>
/// `match blocking reachability` CLI wiring. Task 5 exposes
/// <see cref="Linkuity.Pipeline.ReachabilityDiagnosticService"/> over the CLI; these tests cover
/// routing, the required-profile contract shared with `match corpus audit`, the JSON output's
/// reconciliation identity, and its determinism (no wall-clock anywhere in the artifact).
/// </summary>
public sealed class ReachabilityDiagnosticCommandsTests : IDisposable
{
    private readonly string TempDir;
    private readonly string RecordsPath;
    private readonly string TruthPath;
    private readonly string ProfilePath;

    public ReachabilityDiagnosticCommandsTests()
    {
        TempDir = Path.Combine(Path.GetTempPath(), "linkuity-reach-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempDir);

        RecordsPath = Path.Combine(TempDir, "records.csv");
        File.WriteAllText(RecordsPath,
            "\"id\",\"organization_name\"\n" +
            "\"r0\",\"ACME TRADING LIMITED\"\n" +
            "\"r1\",\"ACME TRADING LIMITED\"\n" +
            "\"r2\",\"ZEBRA CORP\"\n" +
            "\"r3\",\"QUASAR HOLDINGS\"\n");

        // r0/r1 share every fingerprint/token/acronym key -> reachable. r2/r3 ("ZEBRA CORP" /
        // "QUASAR HOLDINGS") share no key and no other column -> unreachable, cause B3 (genuinely
        // disjoint) -- see ReachabilityDiagnosticTests.NoSharedValueFixture for the same trace.
        TruthPath = Path.Combine(TempDir, "truth.csv");
        File.WriteAllText(TruthPath,
            "\"record_id\",\"canonical_key\"\n" +
            "\"r0\",\"acme\"\n\"r1\",\"acme\"\n" +
            "\"r2\",\"zq\"\n\"r3\",\"zq\"\n");

        ProfilePath = Path.Combine(TempDir, "profile.json");
        File.WriteAllText(ProfilePath, """
        {
          "contentType": "organization",
          "fields": [
            { "name": "organization_name", "semanticType": "OrganizationName",
              "roles": ["Searchable","Matchable","Blocking"],
              "similarityEvaluator": "canonical-jaccard", "weight": 4.0 }
          ],
          "normalizationStrategy": "identity",
          "maxBlockSize": 50,
          "blockingStrategies": ["fingerprint","token","acronym"],
          "candidateRetrievalStrategy": "linear",
          "similarityStrategy": "field-weighted",
          "scoringStrategy": "identifier-weighted",
          "decisionStrategy": "threshold",
          "clusteringStrategy": "union-find",
          "autoMatchThreshold": 0.41,
          "reviewThreshold": 0.31
        }
        """);
    }

    public void Dispose()
    {
        try { Directory.Delete(TempDir, recursive: true); } catch { /* best effort */ }
    }

    private string[] BaseArgs =>
    [
        "match", "blocking", "reachability",
        "--input", RecordsPath, "--ground-truth", TruthPath, "--profile", ProfilePath
    ];

    private static async Task<int> RunAsync(string[] args)
    {
        using var outW = new StringWriter();
        using var errW = new StringWriter();
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        Console.SetOut(outW);
        Console.SetError(errW);
        try { return await new LocalBatchRunner().RunAsync(args, CancellationToken.None); }
        finally
        {
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
        }
    }

    [Fact]
    public async Task RequiresAProfile()
    {
        var exit = await RunAsync(["match", "blocking", "reachability",
            "--input", RecordsPath, "--ground-truth", TruthPath]);
        Assert.NotEqual(0, exit);
    }

    [Fact]
    public async Task WritesJsonAndReportAndReconciles()
    {
        var json = Path.Combine(TempDir, "diag.json");
        var exit = await RunAsync(["match", "blocking", "reachability",
            "--input", RecordsPath, "--ground-truth", TruthPath,
            "--profile", ProfilePath, "--json", json]);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(File.ReadAllText(json));
        var root = doc.RootElement;
        var unreachable = root.GetProperty("unreachablePairs").GetInt64();
        var sum = root.GetProperty("causeA").GetProperty("pairCount").GetInt64()
                + root.GetProperty("causeB1").GetProperty("pairCount").GetInt64()
                + root.GetProperty("causeB2").GetProperty("pairCount").GetInt64()
                + root.GetProperty("causeB3").GetProperty("pairCount").GetInt64();
        Assert.Equal(unreachable, sum);
    }

    [Fact]
    public async Task OutputIsByteIdenticalAcrossRuns()
    {
        var a = Path.Combine(TempDir, "a.json");
        var b = Path.Combine(TempDir, "b.json");
        await RunAsync([.. BaseArgs, "--json", a]);
        await RunAsync([.. BaseArgs, "--json", b]);
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
    }
}
