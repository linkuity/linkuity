using System.Globalization;
using System.Text;

namespace Linkuity.Cli.Tests;

public class BlockingSuppressionCliTests
{
    private static (string Csv, string Profile, string GroundTruth) WriteFixture()
    {
        var dir = Directory.CreateTempSubdirectory("lk-supp-cli").FullName;
        var csv = Path.Combine(dir, "orgs.csv");
        // junk-d is a second unrelated "INC" filler record: with engine-parity suppression
        // (corpus frequency = size-1), name:inc needs 6 members (frequency 5 > 4) to remain
        // suppressed at --max-block-size 4.
        File.WriteAllText(csv,
            "id,organization_name\n" +
            "\"acme-a\",\"ACMEWIDGETS INC\"\n" +
            "\"acme-b\",\"AJAX INC\"\n" +
            "\"junk-c\",\"OMEGA INC\"\n" +
            "\"junk-d\",\"DELTA INC\"\n" +
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
        Assert.Contains("Suppressed keys (corpus frequency > 4): 1", output);
        Assert.Contains("name:inc (size 6)", output);
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
        Assert.Contains("suppressed,name:inc,6,token-name", output);
        Assert.Contains("suppression_missed,acme-a,acme-b,acme,", output);
    }

    // 600 entities x 2 records, every name a single globally-unique token, so no two records in
    // the fixture share a token-name key: all 600 entity pairs are unreachable. 600 is STRICTLY
    // MORE than BlockingAuditService.MissedPairSampleCap (500) -- same property as the Pipeline
    // suite's ManyMissedPairsFixture, which is private to that assembly. If it were not more than
    // the cap, the assertion below would pass while proving nothing.
    private static (string Csv, string Profile, string GroundTruth) WriteManyMissedPairsFixture()
    {
        var dir = Directory.CreateTempSubdirectory("lk-missed-cli").FullName;

        var records = new StringBuilder("id,organization_name\n");
        var truth = new StringBuilder("record_id,canonical_key\n");
        for (var i = 0; i < 600; i++)
        {
            records.Append(CultureInfo.InvariantCulture, $"\"ent{i}-l\",\"LEFTUNIQTOKEN{i}\"\n");
            records.Append(CultureInfo.InvariantCulture, $"\"ent{i}-r\",\"RIGHTUNIQTOKEN{i}\"\n");
            truth.Append(CultureInfo.InvariantCulture, $"\"ent{i}-l\",\"ent{i}\"\n");
            truth.Append(CultureInfo.InvariantCulture, $"\"ent{i}-r\",\"ent{i}\"\n");
        }

        var csv = Path.Combine(dir, "orgs.csv");
        File.WriteAllText(csv, records.ToString());
        var gt = Path.Combine(dir, "gt.csv");
        File.WriteAllText(gt, truth.ToString());

        var profile = Path.Combine(dir, "org.profile.json");
        File.WriteAllText(profile, """
            {
              "contentType": "org-missed-count-test",
              "fields": [
                { "name": "organization_name", "semanticType": "OrganizationName", "roles": ["Matchable","Blocking"], "similarityEvaluator": "jaccard", "weight": 4.0 }
              ],
              "normalizationStrategy": "identity",
              "blockingStrategies": ["token-name"],
              "candidateRetrievalStrategy": "blocking-linear",
              "similarityStrategy": "field-weighted",
              "scoringStrategy": "identifier-weighted",
              "decisionStrategy": "threshold",
              "clusteringStrategy": "union-find",
              "autoMatchThreshold": 0.41,
              "reviewThreshold": 0.31
            }
            """);

        return (csv, profile, gt);
    }

    [Fact]
    public async Task Audit_MoreMissedPairsThanTheSampleCap_ReportsTheTrueCountNotTheCap()
    {
        // REGRESSION: the formatter printed r.MissedPairs.Count -- the length of a list capped at
        // 500 -- so `match blocking audit` reported "500" on a corpus with 12,314 missed pairs.
        var (csv, profile, gt) = WriteManyMissedPairsFixture();
        var (exit, output, _) = await RunAsync(
            "match", "blocking", "audit", "--input", csv, "--profile", profile, "--ground-truth", gt);

        Assert.Equal(0, exit);
        Assert.Contains("Missed pairs (no shared blocking key): 600", output);
        Assert.DoesNotContain("Missed pairs (no shared blocking key): 500", output);
        // and the truncation is declared, so 500 rows under a header of 600 cannot be misread
        Assert.Contains("showing a deterministic sample of 500", output);

        var rendered = output.Split('\n').Count(line => line.StartsWith("  [ent", StringComparison.Ordinal));
        Assert.Equal(500, rendered);
    }

    [Fact]
    public async Task Audit_CsvFormat_MissedRowsCarryThePopulationCount()
    {
        // The CSV emitted 500 silently-truncated rows with nothing on them to say so.
        var (csv, profile, gt) = WriteManyMissedPairsFixture();
        var (exit, output, _) = await RunAsync(
            "match", "blocking", "audit", "--input", csv, "--profile", profile,
            "--ground-truth", gt, "--format", "csv");

        Assert.Equal(0, exit);
        Assert.Contains("section,left,right,canonical,left_keys,right_keys,population_count,sampled_count", output);
        var missedRows = output.Split('\n').Where(l => l.StartsWith("missed,", StringComparison.Ordinal)).ToList();
        Assert.Equal(500, missedRows.Count);
        Assert.All(missedRows, row => Assert.EndsWith(",600,500", row.TrimEnd('\r')));
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
        Assert.Contains("SKIPPED (all shared keys suppressed: name:inc (size 6, corpus frequency 5 > 4))", output);
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
