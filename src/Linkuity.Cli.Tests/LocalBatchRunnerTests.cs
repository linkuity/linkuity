using System.Text;
using Linkuity.Cli;

namespace Linkuity.Cli.Tests;

public sealed class LocalBatchRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"linkuity-cli-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task RunAsync_WhenInputIsMissing_ReturnsNonZero()
    {
        var configPath = WriteConfig();
        var outputPath = Path.Combine(_root, "output");
        var runner = new LocalBatchRunner(new FakeMatchingProcess());

        var exitCode = await runner.RunAsync(
            [
                "run",
                "--input", Path.Combine(_root, "missing.csv"),
                "--config", configPath,
                "--output", outputPath
            ],
            CancellationToken.None);

        Assert.NotEqual(0, exitCode);
        Assert.False(Directory.Exists(outputPath));
    }

    [Fact]
    public async Task RunAsync_WhenConfigHasDuplicateFields_ReturnsNonZeroWithoutCreatingOutput()
    {
        var inputPath = WriteInputCsv();
        var configPath = WriteConfig(fieldsJson:
            """
            [
              { "name": "email", "semanticType": "email" },
              { "name": "EMAIL", "semanticType": "email" }
            ]
            """);
        var outputPath = Path.Combine(_root, "invalid-output");
        var runner = new LocalBatchRunner(new FakeMatchingProcess());

        var exitCode = await runner.RunAsync(
            [
                "run",
                "--input", inputPath,
                "--config", configPath,
                "--output", outputPath
            ],
            CancellationToken.None);

        Assert.NotEqual(0, exitCode);
        Assert.False(Directory.Exists(outputPath));
    }

    [Fact]
    public async Task RunAsync_WhenConfigHasInvalidContentType_ReturnsNonZeroWithoutCreatingOutput()
    {
        var inputPath = WriteInputCsv();
        var configPath = WriteConfig(contentType: "unsupported");
        var outputPath = Path.Combine(_root, "invalid-content-type-output");
        var runner = new LocalBatchRunner(new FakeMatchingProcess());

        var exitCode = await runner.RunAsync(
            [
                "run",
                "--input", inputPath,
                "--config", configPath,
                "--output", outputPath
            ],
            CancellationToken.None);

        Assert.NotEqual(0, exitCode);
        Assert.False(Directory.Exists(outputPath));
    }

    [Fact]
    public async Task RunAsync_WithValidInput_WritesGoldenRecordsAndLocalArtifacts()
    {
        var inputPath = WriteInputCsv();
        var configPath = WriteConfig();
        var outputPath = Path.Combine(_root, "output");
        var runner = new LocalBatchRunner(new FakeMatchingProcess());

        var exitCode = await runner.RunAsync(
            [
                "run",
                "--input", inputPath,
                "--config", configPath,
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
        var configPath = WriteConfig();
        var outputPath = Path.Combine(_root, "neo4j-output");
        var runner = new LocalBatchRunner(new FakeMatchingProcess());

        var exitCode = await runner.RunAsync(
            [
                "run",
                "--input", inputPath,
                "--config", configPath,
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

    private string WriteConfig(
        string contentType = "person",
        string fieldsJson = """
            [
              { "name": "source", "semanticType": "source_identifier", "participatesInMatching": false },
              { "name": "name", "semanticType": "full_name" },
              { "name": "email", "semanticType": "email" },
              { "name": "phone", "semanticType": "phone" }
            ]
            """)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "match-config.json");
        File.WriteAllText(path,
            $$"""
            {
              "configuration": {
                "contentType": "{{contentType}}",
                "fields": {{fieldsJson}}
              },
              "mergeConfiguration": {
                "mergeFields": [
                  { "fieldName": "email", "sourcePriority": ["CRM", "Marketing"] },
                  { "fieldName": "phone", "sourcePriority": ["CRM", "Marketing"] }
                ]
              },
              "autoStart": true
            }
            """,
            Encoding.UTF8);
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

    private sealed class FakeMatchingProcess : IMatchingProcess
    {
        public Task RunAsync(string artifactRoot, string jobId, CancellationToken ct)
        {
            var matchesPath = Path.Combine(artifactRoot, jobId, "matches.csv");
            Directory.CreateDirectory(Path.GetDirectoryName(matchesPath)!);
            return File.WriteAllTextAsync(
                matchesPath,
                """
                left_id,right_id,similarity,fuzzy_similarity
                1,2,0.99,0.99
                """,
                ct);
        }
    }
}
