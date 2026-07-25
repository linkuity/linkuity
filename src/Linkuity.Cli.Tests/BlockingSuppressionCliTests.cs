namespace Linkuity.Cli.Tests;

public class BlockingSuppressionCliTests
{
    private static (string Csv, string Profile, string GroundTruth) WriteFixture()
    {
        var dir = Directory.CreateTempSubdirectory("lk-supp-cli").FullName;
        var csv = Path.Combine(dir, "orgs.csv");
        File.WriteAllText(csv,
            "id,organization_name\n" +
            "\"acme-a\",\"ACMEWIDGETS INC\"\n" +
            "\"acme-b\",\"AJAX INC\"\n" +
            "\"junk-c\",\"OMEGA INC\"\n" +
            "\"zeta-a\",\"ZETA GLOBAL INC\"\n" +
            "\"zeta-b\",\"ZETA HOLDINGS INC\"\n");

        var profile = Path.Combine(dir, "org.profile.json");
        File.WriteAllText(profile, """
            {
              "contentType": "org-suppression-test",
              "fields": [
                { "name": "organization_name", "semanticType": "OrganizationName", "roles": ["Matchable","Blocking"], "similarityEvaluator": "jaccard", "weight": 4.0 }
              ],
              "normalizationStrategy": "identity",
              "blockingStrategies": ["token-name", "prefix"],
              "candidateRetrievalStrategy": "blocking-linear",
              "similarityStrategy": "field-weighted",
              "scoringStrategy": "identifier-weighted",
              "decisionStrategy": "threshold",
              "clusteringStrategy": "union-find",
              "autoMatchThreshold": 0.41,
              "reviewThreshold": 0.31
            }
            """);

        var gt = Path.Combine(dir, "gt.csv");
        File.WriteAllText(gt,
            "record_id,canonical_key\n" +
            "\"acme-a\",\"acme\"\n\"acme-b\",\"acme\"\n" +
            "\"zeta-a\",\"zeta\"\n\"zeta-b\",\"zeta\"\n");

        return (csv, profile, gt);
    }

    private static async Task<(int Exit, string Out, string Err)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var origOut = Console.Out;
        var origErr = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var exit = await new LocalBatchRunner().RunAsync(args, CancellationToken.None);
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }
    }

    [Fact]
    public async Task Audit_MaxBlockSize_ReportsSuppressionAndDualCeiling()
    {
        var (csv, profile, gt) = WriteFixture();
        var (exit, output, _) = await RunAsync(
            "match", "blocking", "audit", "--input", csv, "--profile", profile,
            "--ground-truth", gt, "--max-block-size", "4");

        Assert.Equal(0, exit);
        Assert.Contains("Suppressed keys (block size > 4): 1", output);
        Assert.Contains("name:inc (size 5)", output);
        Assert.Contains("Effective recall ceiling: 50.0 % (1/2)", output);
        Assert.Contains("pairs lost to suppression: 1", output);
        Assert.Contains("[acme]", output);
    }

    [Fact]
    public async Task Audit_MinRecall_GatesOnEffectiveCeiling()
    {
        var (csv, profile, gt) = WriteFixture();
        // Raw ceiling is 100% but effective is 50% -> the gate must fail.
        var (exit, _, err) = await RunAsync(
            "match", "blocking", "audit", "--input", csv, "--profile", profile,
            "--ground-truth", gt, "--max-block-size", "4", "--min-recall", "0.9");

        Assert.Equal(1, exit);
        Assert.Contains("below the required minimum", err);
    }

    [Fact]
    public async Task Audit_CsvFormat_EmitsDeterministicSuppressionRows()
    {
        var (csv, profile, gt) = WriteFixture();
        var (exit, output, _) = await RunAsync(
            "match", "blocking", "audit", "--input", csv, "--profile", profile,
            "--ground-truth", gt, "--max-block-size", "4", "--format", "csv");

        Assert.Equal(0, exit);
        Assert.Contains("suppressed,name:inc,5,token-name", output);
        Assert.Contains("suppression_missed,acme-a,acme-b,acme,", output);
    }

    [Fact]
    public async Task Audit_InvalidMaxBlockSize_Exit2()
    {
        var (csv, profile, _) = WriteFixture();
        var (exit, _, err) = await RunAsync(
            "match", "blocking", "audit", "--input", csv, "--profile", profile, "--max-block-size", "0");

        Assert.Equal(2, exit);
        Assert.Contains("Invalid --max-block-size", err);
    }

    [Fact]
    public async Task Explain_SuppressedSharedKey_IsAnnotatedNotCounted()
    {
        var (csv, profile, _) = WriteFixture();
        var (exit, output, _) = await RunAsync(
            "match", "blocking", "explain", "--input", csv, "--profile", profile,
            "--left", "acme-a", "--right", "acme-b", "--max-block-size", "4");

        Assert.Equal(0, exit);
        Assert.Contains("SKIPPED (all shared keys suppressed: name:inc (size 5 > 4))", output);
        Assert.DoesNotContain("WOULD COMPARE", output);
    }

    [Fact]
    public async Task Explain_ActiveSharedKeySurvivesSuppression_WouldCompare()
    {
        var (csv, profile, _) = WriteFixture();
        var (exit, output, _) = await RunAsync(
            "match", "blocking", "explain", "--input", csv, "--profile", profile,
            "--left", "zeta-a", "--right", "zeta-b", "--max-block-size", "4");

        Assert.Equal(0, exit);
        Assert.Contains("WOULD COMPARE", output);
        Assert.Contains("prefix:zeta", output);
        Assert.DoesNotContain("name:inc", output.Split("WOULD COMPARE")[1]); // suppressed key not listed as a comparison path
    }
}
