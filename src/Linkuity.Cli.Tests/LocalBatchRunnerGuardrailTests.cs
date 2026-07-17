using System.Text;
using Linkuity.Cli;
using Linkuity.Infrastructure.Local;

namespace Linkuity.Cli.Tests;

/// <summary>
/// Verifies the row-count guardrail on durable CLI read-back commands (Task 17 / Milestone 23).
/// The guardrail loads the primary collection, checks its count, and fails with exit code 3
/// before performing secondary in-memory joins when the count exceeds --max-readback-rows.
/// </summary>
public sealed class LocalBatchRunnerGuardrailTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"linkuity-guardrail-{Guid.NewGuid():N}");

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static LocalBatchRunner NewRunner() => new(new NoOpMatchingProcess());

    /// <summary>Captures stdout, stderr, and exit code without throwing on non-zero exits.</summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> CaptureAllAsync(
        LocalBatchRunner runner, string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var exit = await runner.RunAsync(args, CancellationToken.None);
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
        }
    }

    /// <summary>
    /// Seeds a project with two distinct golden records (two clusters) so the guardrail
    /// can be triggered by setting --max-readback-rows 1.
    /// </summary>
    private async Task<(string MetadataPath, Guid ProjectId)> SeedTwoGoldenRecordsAsync()
    {
        var metadataPath = Path.Combine(_root, "metadata.json");
        var artifactRoot = Path.Combine(_root, "artifacts");
        var jobId = Guid.NewGuid();
        var runner = NewRunner();

        await runner.RunAsync(
            ["project", "create", "--metadata", metadataPath, "--name", "Guardrail MDM", "--content-type", "person"],
            CancellationToken.None);

        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = metadataPath });
        var project = Assert.Single(await store.ListProjectsAsync(CancellationToken.None));

        await runner.RunAsync(
            ["source", "create", "--metadata", metadataPath, "--project-id", project.Id.ToString(), "--name", "Src"],
            CancellationToken.None);
        var source = Assert.Single(await store.ListSourcesAsync(project.Id, CancellationToken.None));

        await runner.RunAsync(
            ["batch", "create", "--metadata", metadataPath,
             "--project-id", project.Id.ToString(),
             "--source-id", source.Id.ToString(),
             "--job-id", jobId.ToString(),
             "--record-count", "2"],
            CancellationToken.None);
        var batch = Assert.Single(await store.ListIngestBatchesAsync(project.Id, CancellationToken.None));

        // Two separate clusters → two golden records in the read-back.
        SeedTwoClusterArtifacts(artifactRoot, jobId);

        await runner.RunAsync(
            ["persist-batch",
             "--metadata", metadataPath,
             "--artifact-root", artifactRoot,
             "--job-id", jobId.ToString(),
             "--project-id", project.Id.ToString(),
             "--source-id", source.Id.ToString(),
             "--batch-id", batch.Id.ToString()],
            CancellationToken.None);

        return (metadataPath, project.Id);
    }

    private static void SeedTwoClusterArtifacts(string artifactRoot, Guid jobId)
    {
        var jobPath = Path.Combine(artifactRoot, jobId.ToString());
        Directory.CreateDirectory(jobPath);
        File.WriteAllText(Path.Combine(jobPath, "metadata.json"), $$$"""{"id":"{{{jobId}}}"}""", Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(jobPath, "normalized.csv"),
            """
            id,source,name,email
            alice-001,CRM,Alice,alice@example.com
            bob-001,CRM,Bob,bob@example.com
            """,
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(jobPath, "matches.csv"),
            """
            left_id,right_id,similarity
            """,
            Encoding.UTF8);
        // Two clusters → two golden records.
        File.WriteAllText(
            Path.Combine(jobPath, "golden_records.csv"),
            """
            cluster_id,record_count,member_ids,email,name
            00000000-0000-0000-0000-000000000001,1,alice-001,alice@example.com,Alice
            00000000-0000-0000-0000-000000000002,1,bob-001,bob@example.com,Bob
            """,
            Encoding.UTF8);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GoldenList_GuardrailFires_WhenPrimaryExceedsMaxRows()
    {
        var (metadataPath, projectId) = await SeedTwoGoldenRecordsAsync();

        // Project has 2 golden records; limit to 1 → guardrail must fire.
        var (exit, stdout, stderr) = await CaptureAllAsync(NewRunner(),
        [
            "golden", "list",
            "--metadata", metadataPath,
            "--project-id", projectId.ToString(),
            "--max-readback-rows", "1"
        ]);

        Assert.Equal(3, exit);
        Assert.Contains("Milestone 24", stderr);
        // No valid CSV rows should have been written to stdout.
        Assert.DoesNotContain("alice@example.com", stdout);
    }

    [Fact]
    public async Task GoldenList_Succeeds_WhenBelowMaxRows()
    {
        var (metadataPath, projectId) = await SeedTwoGoldenRecordsAsync();

        // Default 100 000 limit → normal output, exit 0.
        var (exit, stdout, _) = await CaptureAllAsync(NewRunner(),
        [
            "golden", "list",
            "--metadata", metadataPath,
            "--project-id", projectId.ToString()
        ]);

        Assert.Equal(0, exit);
        Assert.Contains("cluster_id,version,record_count,member_ids", stdout);
        Assert.Contains("alice@example.com", stdout);
        Assert.Contains("bob@example.com", stdout);
    }

    [Fact]
    public async Task ClusterList_GuardrailFires_WhenMaxRowsIsZero()
    {
        var (metadataPath, projectId) = await SeedTwoGoldenRecordsAsync();

        // Any non-empty project exceeds a limit of 0.
        var (exit, stdout, stderr) = await CaptureAllAsync(NewRunner(),
        [
            "cluster", "list",
            "--metadata", metadataPath,
            "--project-id", projectId.ToString(),
            "--max-readback-rows", "0"
        ]);

        Assert.Equal(3, exit);
        Assert.Contains("Milestone 24", stderr);
        Assert.DoesNotContain("cluster_id", stdout);
    }

    [Fact]
    public async Task ClusterList_Succeeds_WhenBelowMaxRows()
    {
        var (metadataPath, projectId) = await SeedTwoGoldenRecordsAsync();

        var (exit, stdout, _) = await CaptureAllAsync(NewRunner(),
        [
            "cluster", "list",
            "--metadata", metadataPath,
            "--project-id", projectId.ToString(),
            "--max-readback-rows", "100"
        ]);

        Assert.Equal(0, exit);
        Assert.Contains("cluster_id,record_count,member_ids", stdout);
    }

    [Fact]
    public async Task GoldenHistory_GuardrailFires_WhenExceedsMaxRows()
    {
        var (metadataPath, projectId) = await SeedTwoGoldenRecordsAsync();

        // Two clusters → two version rows; limit to 1 → guardrail fires.
        var (exit, stdout, stderr) = await CaptureAllAsync(NewRunner(),
        [
            "golden", "history",
            "--metadata", metadataPath,
            "--project-id", projectId.ToString(),
            "--max-readback-rows", "1"
        ]);

        Assert.Equal(3, exit);
        Assert.Contains("Milestone 24", stderr);
        Assert.DoesNotContain("alice@example.com", stdout);
    }

    [Fact]
    public async Task GoldenList_NonNumericMaxReadbackRows_FailsGracefully()
    {
        var (metadataPath, projectId) = await SeedTwoGoldenRecordsAsync();

        var (exit, stdout, stderr) = await CaptureAllAsync(NewRunner(),
        [
            "golden", "list",
            "--metadata", metadataPath,
            "--project-id", projectId.ToString(),
            "--max-readback-rows", "abc"
        ]);

        Assert.NotEqual(0, exit);
        // Clear, actionable message on stderr — no unhandled FormatException / stack trace.
        Assert.Contains("--max-readback-rows must be an integer", stderr);
        Assert.Contains("abc", stderr);
        Assert.DoesNotContain("Exception", stdout);
        Assert.DoesNotContain("at Linkuity", stdout);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class NoOpMatchingProcess : IMatchingProcess
    {
        public Task RunAsync(string artifactRoot, string jobId, CancellationToken ct) => Task.CompletedTask;
    }
}
