using System.Text;
using Linkuity.Cli;

namespace Linkuity.Cli.Tests;

public sealed class LocalBatchRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"linkuity-cli-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task RunAsync_WhenInputIsMissing_ReturnsNonZero()
    {
        var profilePath = WriteProfile();
        var mergePath = WriteMergePolicy();
        var outputPath = Path.Combine(_root, "output");
        var runner = new LocalBatchRunner();

        var exitCode = await runner.RunAsync(
            [
                "run",
                "--input", Path.Combine(_root, "missing.csv"),
                "--profile", profilePath,
                "--merge-policy", mergePath,
                "--output", outputPath
            ],
            CancellationToken.None);

        Assert.NotEqual(0, exitCode);
        Assert.False(Directory.Exists(outputPath));
    }

    [Fact]
    public async Task RunAsync_WhenProfileFileMissing_ReturnsNonZeroWithoutCreatingOutput()
    {
        var inputPath = WriteInputCsv();
        var outputPath = Path.Combine(_root, "missing-profile-output");
        var runner = new LocalBatchRunner();
        var exit = await runner.RunAsync(
            ["run", "--input", inputPath, "--profile", "no-such-profile", "--output", outputPath],
            CancellationToken.None);
        Assert.NotEqual(0, exit);
        Assert.False(Directory.Exists(outputPath));
    }

    [Fact]
    public async Task RunAsync_WhenProfileHasDuplicateFields_ReturnsNonZeroWithoutCreatingOutput()
    {
        var inputPath = WriteInputCsv();
        var profilePath = WriteProfile(fieldsJson: """
            [
              { "name": "email", "semanticType": "Email", "roles": ["Matchable"], "similarityEvaluator": "exact" },
              { "name": "EMAIL", "semanticType": "Email", "roles": ["Matchable"], "similarityEvaluator": "exact" }
            ]
            """);
        var outputPath = Path.Combine(_root, "dup-output");
        var runner = new LocalBatchRunner();
        var exit = await runner.RunAsync(
            ["run", "--input", inputPath, "--profile", profilePath, "--output", outputPath],
            CancellationToken.None);
        Assert.NotEqual(0, exit);
        Assert.False(Directory.Exists(outputPath));
    }

    [Fact]
    public async Task RunAsync_BuiltInProfileByName_Resolves()
    {
        var inputPath = WriteInputCsv();
        var outputPath = Path.Combine(_root, "builtin-output");
        var runner = new LocalBatchRunner();
        var exit = await runner.RunAsync(
            ["run", "--input", inputPath, "--profile", "person", "--output", outputPath],
            CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(outputPath, "golden-records.csv")));
    }

    [Fact]
    public async Task RunAsync_WithValidInput_WritesGoldenRecordsAndLocalArtifacts()
    {
        var inputPath = WriteInputCsv();
        var profilePath = WriteProfile();
        var mergePath = WriteMergePolicy();
        var outputPath = Path.Combine(_root, "output");
        var runner = new LocalBatchRunner();

        var exitCode = await runner.RunAsync(
            [
                "run",
                "--input", inputPath,
                "--profile", profilePath,
                "--merge-policy", mergePath,
                "--output", outputPath
            ],
            CancellationToken.None);

        Assert.Equal(0, exitCode);

        var goldenRecordsPath = Path.Combine(outputPath, "golden-records.csv");
        Assert.True(File.Exists(goldenRecordsPath));
        var goldenRecords = await File.ReadAllTextAsync(goldenRecordsPath);
        Assert.Contains("alice@example.com", goldenRecords);
        Assert.Contains("1|2", goldenRecords);

        var artifactRoot = Path.Combine(outputPath, "artifacts");
        var jobDirectory = Assert.Single(Directory.GetDirectories(artifactRoot));
        Assert.True(File.Exists(Path.Combine(jobDirectory, "metadata.json")));
        Assert.True(File.Exists(Path.Combine(jobDirectory, "input.csv")));
        Assert.True(File.Exists(Path.Combine(jobDirectory, "normalized.csv")));
        Assert.True(File.Exists(Path.Combine(jobDirectory, "matches.csv")));
        Assert.True(File.Exists(Path.Combine(jobDirectory, "golden_records.csv")));
    }

    [Fact]
    public async Task RunAsync_WithNeo4jExport_WritesZip()
    {
        var inputPath = WriteInputCsv();
        var profilePath = WriteProfile();
        var mergePath = WriteMergePolicy();
        var outputPath = Path.Combine(_root, "neo4j-output");
        var runner = new LocalBatchRunner();

        var exitCode = await runner.RunAsync(
            [
                "run",
                "--input", inputPath,
                "--profile", profilePath,
                "--merge-policy", mergePath,
                "--output", outputPath,
                "--neo4j-export"
            ],
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(outputPath, "neo4j-export.zip")));
    }

    [Fact]
    public async Task RunScenarioScript_ResolvesCliProjectFromScriptLocation()
    {
        var scriptPath = FindRepositoryFile("scripts", "Run-Scenario.ps1");
        var script = await File.ReadAllTextAsync(scriptPath);

        Assert.Contains("$repoRoot", script);
        Assert.Contains("$cliProjectPath", script);
        Assert.Contains("--project", script);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string WriteInputCsv()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "sample.csv");
        File.WriteAllText(path,
            """
            id,source,name,email,phone
            1,CRM,Alice,ALICE@EXAMPLE.COM,(800) 555-0100
            2,Marketing,Alice M,alice@example.com,8005550100
            3,CRM,Bob,bob@example.com,(800) 555-0199
            """,
            Encoding.UTF8);
        return path;
    }

    private string WriteProfile(string contentType = "person", string? fieldsJson = null)
    {
        Directory.CreateDirectory(_root);
        var fields = fieldsJson ?? """
            [
              { "name": "source", "semanticType": "SourceIdentifier", "roles": [] },
              { "name": "name", "semanticType": "FullName", "roles": ["Searchable","Matchable","Blocking"], "similarityEvaluator": "fuzzy", "weight": 1.5 },
              { "name": "email", "semanticType": "Email", "roles": ["Searchable","Matchable","Blocking","Identifier"], "similarityEvaluator": "exact", "weight": 3.0 },
              { "name": "phone", "semanticType": "Phone", "roles": ["Matchable","Blocking","Identifier"], "similarityEvaluator": "exact", "weight": 3.0 }
            ]
            """;
        var path = Path.Combine(_root, "run.profile.json");
        File.WriteAllText(path,
            $$"""
            {
              "contentType": "{{contentType}}",
              "fields": {{fields}},
              "normalizationStrategy": "identity",
              "blockingStrategies": ["exact-value", "token-name"],
              "candidateRetrievalStrategy": "linear",
              "similarityStrategy": "field-weighted",
              "scoringStrategy": "identifier-weighted",
              "decisionStrategy": "threshold",
              "clusteringStrategy": "union-find",
              "autoMatchThreshold": 0.90,
              "reviewThreshold": 0.75
            }
            """, Encoding.UTF8);
        return path;
    }

    private string WriteMergePolicy()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "run.merge.json");
        File.WriteAllText(path, """
            { "mergeFields": [
              { "fieldName": "email", "sourcePriority": ["CRM", "Marketing"] },
              { "fieldName": "phone", "sourcePriority": ["CRM", "Marketing"] }
            ] }
            """, Encoding.UTF8);
        return path;
    }

    private static string FindRepositoryFile(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(new[] { directory.FullName }.Concat(pathParts).ToArray());
            if (File.Exists(path))
                return path;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file {Path.Combine(pathParts)}.");
    }
}
