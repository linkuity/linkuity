using Linkuity.Cli;

namespace Linkuity.Cli.Tests;

public class LocalBatchRunnerBlockingTests
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

    // A tiny CSV + org profile written to temp files. Boeing is the guaranteed missed pair.
    private static (string Csv, string Profile, string GroundTruth) WriteFixture()
    {
        var dir = Path.Combine(Path.GetTempPath(), "linkuity-blocking-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var csv = Path.Combine(dir, "companies.csv");
        File.WriteAllText(csv,
            "\"id\",\"source\",\"organization_name\"\n" +
            "\"apple-1\",\"GLEIF\",\"APPLE INC\"\n" +
            "\"apple-2\",\"SEC\",\"APPLE INCORPORATED\"\n" +
            "\"boe-gleif\",\"GLEIF\",\"THE BOEING COMPANY\"\n" +
            "\"boe-sec\",\"SEC\",\"BOEING CO\"\n");

        var profile = Path.Combine(dir, "org.profile.json");
        File.WriteAllText(profile, """
        {
          "contentType": "organization",
          "fields": [
            { "name": "organization_name", "semanticType": "OrganizationName",
              "roles": ["Searchable","Matchable","Blocking"], "similarityEvaluator": "jaccard", "weight": 4.0 }
          ],
          "normalizationStrategy": "identity",
          "blockingStrategies": ["exact-value","token-name","prefix"],
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
            "\"apple-1\",\"apple\"\n\"apple-2\",\"apple\"\n" +
            "\"boe-gleif\",\"boeing\"\n\"boe-sec\",\"boeing\"\n");

        return (csv, profile, gt);
    }

    [Fact]
    public async Task Audit_Csv_WithGroundTruth_PrintsRecallMissedPairAndLargestBlock()
    {
        var (csv, profile, gt) = WriteFixture();

        var (exit, output, _) = await RunAsync(
        [
            "match", "blocking", "audit",
            "--input", csv, "--profile", profile, "--ground-truth", gt
        ]);

        Assert.Equal(0, exit);
        Assert.Contains("Recall ceiling", output);
        Assert.Contains("boe-gleif", output);   // missed pair endpoint
        Assert.Contains("boe-sec", output);
        Assert.Contains("Largest blocks", output);
    }

    [Fact]
    public async Task Explain_Pair_WithNoSharedKey_PrintsSkipped()
    {
        var (csv, profile, _) = WriteFixture();

        var (exit, output, _) = await RunAsync(
        [
            "match", "blocking", "explain",
            "--input", csv, "--profile", profile, "--left", "boe-gleif", "--right", "boe-sec"
        ]);

        Assert.Equal(0, exit);
        Assert.Contains("SKIPPED", output);
    }

    [Fact]
    public async Task Explain_Pair_Sharing_PrintsWouldCompare()
    {
        var (csv, profile, _) = WriteFixture();

        var (exit, output, _) = await RunAsync(
        [
            "match", "blocking", "explain",
            "--input", csv, "--profile", profile, "--left", "apple-1", "--right", "apple-2"
        ]);

        Assert.Equal(0, exit);
        Assert.Contains("WOULD COMPARE", output);
    }

    [Fact]
    public async Task Audit_MissingSource_Exits2WithMessage()
    {
        var (_, profile, _) = WriteFixture();

        var (exit, _, err) = await RunAsync(["match", "blocking", "audit", "--profile", profile]);

        Assert.Equal(2, exit);
        Assert.Contains("source", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Audit_FileStoreSource_ReadsAllProjectRecords()
    {
        var (_, profile, _) = WriteFixture();
        var storePath = Path.Combine(Path.GetTempPath(), "linkuity-blk-store-" + Guid.NewGuid().ToString("N") + ".json");

        var store = new Linkuity.Infrastructure.Local.FileMetadataStore(
            new Linkuity.Infrastructure.Local.FileMetadataStoreOptions { DatabasePath = storePath });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Blk", "organization", null, now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "SEC", now, CancellationToken.None);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 2, now, CancellationToken.None);
        Linkuity.Core.Models.EntityRecord Rec(string srid, string name) => new()
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, SourceId = source.Id, IngestBatchId = batch.Id,
            SourceRecordId = srid,
            Fields = new Dictionary<string, string> { ["organization_name"] = name },
            CreatedAt = now
        };
        await store.SaveCompletedBatchAsync(
            new Linkuity.Core.Models.CompletedBatchMetadata(
                [Rec("boe-gleif", "THE BOEING COMPANY"), Rec("boe-sec", "BOEING CO")], [], [], [], []),
            CancellationToken.None);

        var (exit, output, _) = await RunAsync(
        [
            "match", "blocking", "explain",
            "--metadata", storePath, "--project-id", project.Id.ToString(),
            "--profile", profile, "--left", "boe-gleif", "--right", "boe-sec"
        ]);

        Assert.Equal(0, exit);
        Assert.Contains("SKIPPED", output);
    }

    [Fact]
    public async Task Audit_MinRecallAboveActual_ExitsNonZero()
    {
        var (csv, profile, gt) = WriteFixture(); // actual recall is 0.5 (apple reachable, boeing missed)

        var (exit, _, _) = await RunAsync(
        [
            "match", "blocking", "audit",
            "--input", csv, "--profile", profile, "--ground-truth", gt, "--min-recall", "0.9"
        ]);

        Assert.NotEqual(0, exit);
    }

    [Fact]
    public async Task Audit_MinRecallBelowActual_ExitsZero()
    {
        var (csv, profile, gt) = WriteFixture();

        var (exit, _, _) = await RunAsync(
        [
            "match", "blocking", "audit",
            "--input", csv, "--profile", profile, "--ground-truth", gt, "--min-recall", "0.4"
        ]);

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Audit_FormatCsv_EmitsMachineRows()
    {
        var (csv, profile, gt) = WriteFixture();

        var (exit, output, _) = await RunAsync(
        [
            "match", "blocking", "audit",
            "--input", csv, "--profile", profile, "--ground-truth", gt, "--format", "csv"
        ]);

        Assert.Equal(0, exit);
        Assert.Contains("section,key,size,strategies", output); // block table header
        Assert.Contains("missed,", output);                     // missed-pair rows tagged
    }
}
