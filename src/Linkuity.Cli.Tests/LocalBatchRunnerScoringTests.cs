using Linkuity.Cli;

namespace Linkuity.Cli.Tests;

public class LocalBatchRunnerScoringTests
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

    // Showcase-shaped org profile (organization_name/address_line/postal_code, jaccard/jaccard/exact,
    // thresholds 0.41/0.31) matching ScoringAuditServiceTests.OrgProfile, but with the
    // blockingStrategies this CLI fixture actually exercises: exact-value + token-name.
    // a1/a2 are identical "APEX ENERGY" (share the token-name last-token key "energy" -> one
    // candidate pair); a3 "APEX MINERALS" shares no blocking key with either.
    private static (string Csv, string Profile, string GroundTruth) WriteFixture()
    {
        var dir = Path.Combine(Path.GetTempPath(), "linkuity-scoring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var csv = Path.Combine(dir, "companies.csv");
        File.WriteAllText(csv,
            "\"id\",\"organization_name\"\n" +
            "\"a1\",\"APEX ENERGY\"\n" +
            "\"a2\",\"APEX ENERGY\"\n" +
            "\"a3\",\"APEX MINERALS\"\n");

        var profile = Path.Combine(dir, "org.profile.json");
        File.WriteAllText(profile, """
        {
          "contentType": "organization",
          "fields": [
            { "name": "organization_name", "semanticType": "OrganizationName",
              "roles": ["Searchable","Matchable","Blocking"], "similarityEvaluator": "jaccard", "weight": 4.0 },
            { "name": "address_line", "semanticType": "AddressLine",
              "roles": ["Searchable","Matchable"], "similarityEvaluator": "jaccard", "weight": 2.5 },
            { "name": "postal_code", "semanticType": "PostalCode",
              "roles": ["Matchable"], "similarityEvaluator": "exact", "weight": 0.5 }
          ],
          "normalizationStrategy": "identity",
          "blockingStrategies": ["exact-value","token-name"],
          "candidateRetrievalStrategy": "blocking-linear",
          "similarityStrategy": "field-weighted",
          "scoringStrategy": "identifier-weighted",
          "decisionStrategy": "threshold",
          "clusteringStrategy": "union-find",
          "autoMatchThreshold": 0.41,
          "reviewThreshold": 0.31
        }
        """);

        var gt = Path.Combine(dir, "ground-truth.csv");
        File.WriteAllText(gt,
            "\"record_id\",\"canonical_key\"\n" +
            "\"a1\",\"apex\"\n\"a2\",\"apex\"\n\"a3\",\"minerals\"\n");

        return (csv, profile, gt);
    }

    [Fact]
    public async Task ScoringAudit_TextReport_PrintsBandsAndMetrics()
    {
        var (csv, profile, gt) = WriteFixture();

        var (exit, output, _) = await RunAsync(
        [
            "match", "scoring", "audit",
            "--input", csv, "--profile", profile, "--ground-truth", gt
        ]);

        Assert.Equal(0, exit);
        Assert.Contains("Bands:", output);
        Assert.Contains("precision", output);
        Assert.Contains("recall", output);
        Assert.Contains("Sweep", output);
        Assert.Contains("Miss decomposition", output);
    }

    [Fact]
    public async Task ScoringAudit_WithoutGroundTruth_OmitsMetricSections()
    {
        var (csv, profile, _) = WriteFixture();

        var (exit, output, _) = await RunAsync(
        [
            "match", "scoring", "audit",
            "--input", csv, "--profile", profile
        ]);

        Assert.Equal(0, exit);
        Assert.Contains("Bands:", output);
        Assert.DoesNotContain("precision", output);
        Assert.DoesNotContain("Sweep", output);
    }

    [Fact]
    public async Task ScoringAudit_ThresholdOverrides_Validated()
    {
        var (csv, profile, _) = WriteFixture();

        var (exit, _, err) = await RunAsync(
        [
            "match", "scoring", "audit",
            "--input", csv, "--profile", profile,
            "--auto-threshold", "0.3", "--review-threshold", "0.5"
        ]);

        Assert.Equal(2, exit);
        Assert.Contains("review", err);
    }

    [Fact]
    public async Task ScoringAudit_DuplicateInputId_Exit2()
    {
        var (_, profile, _) = WriteFixture();
        var dir = Path.Combine(Path.GetTempPath(), "linkuity-scoring-dup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var csv = Path.Combine(dir, "dup.csv");
        File.WriteAllText(csv,
            "\"id\",\"organization_name\"\n" +
            "\"dup\",\"APEX ENERGY\"\n" +
            "\"dup\",\"APEX MINERALS\"\n");

        var (exit, _, err) = await RunAsync(
        [
            "match", "scoring", "audit",
            "--input", csv, "--profile", profile
        ]);

        Assert.Equal(2, exit);
        Assert.Contains("dup", err);
    }

    [Fact]
    public async Task ScoringAudit_DuplicateGroundTruthId_Exit2()
    {
        var (csv, profile, _) = WriteFixture();
        var dir = Path.Combine(Path.GetTempPath(), "linkuity-scoring-dupgt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var gt = Path.Combine(dir, "ground-truth.csv");
        File.WriteAllText(gt,
            "\"record_id\",\"canonical_key\"\n" +
            "\"a1\",\"apex\"\n\"a1\",\"apex\"\n");

        var (exit, _, err) = await RunAsync(
        [
            "match", "scoring", "audit",
            "--input", csv, "--profile", profile, "--ground-truth", gt
        ]);

        Assert.Equal(2, exit);
        Assert.Contains("a1", err);
    }

    [Fact]
    public async Task ScoringAudit_UnsupportedProfileStrategy_Exit2()
    {
        var (csv, _, _) = WriteFixture();
        var dir = Path.Combine(Path.GetTempPath(), "linkuity-scoring-unsupported-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var profile = Path.Combine(dir, "unsupported.profile.json");
        File.WriteAllText(profile, """
        {
          "contentType": "organization",
          "fields": [
            { "name": "organization_name", "semanticType": "OrganizationName",
              "roles": ["Searchable","Matchable","Blocking"], "similarityEvaluator": "jaccard", "weight": 4.0 }
          ],
          "normalizationStrategy": "identity",
          "blockingStrategies": ["exact-value","token-name"],
          "candidateRetrievalStrategy": "blocking-linear",
          "similarityStrategy": "default",
          "scoringStrategy": "default",
          "decisionStrategy": "threshold",
          "clusteringStrategy": "union-find",
          "autoMatchThreshold": 0.41,
          "reviewThreshold": 0.31
        }
        """);

        var (exit, _, err) = await RunAsync(
        [
            "match", "scoring", "audit",
            "--input", csv, "--profile", profile
        ]);

        // Either the profile loader or the service's v1 strategy constraint may reject this;
        // the exit code contract is what matters (see task brief note on case 6).
        Assert.Equal(2, exit);
        Assert.Contains("field-weighted", err);
    }

    [Fact]
    public async Task ScoringAudit_UnknownVerb_Exit2()
    {
        var (csv, profile, _) = WriteFixture();

        var (exit, _, err) = await RunAsync(
        [
            "match", "scoring", "frobnicate",
            "--input", csv, "--profile", profile
        ]);

        Assert.Equal(2, exit);
        Assert.Contains("audit", err, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("explain", err, StringComparison.OrdinalIgnoreCase);
    }
}
