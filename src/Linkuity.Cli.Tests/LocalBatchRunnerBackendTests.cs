using System.Text;
using Linkuity.Cli;
using Linkuity.TestSupport;
using Linkuity.Infrastructure.Local;
using Testcontainers.PostgreSql;

namespace Linkuity.Cli.Tests;

/// <summary>
/// Tests for the --metadata-store backend selection added in M23.
/// Non-gated: default (file) path behaves exactly as before.
/// Gated [SkippableFact]: postgres path via Testcontainers (requires Docker).
/// </summary>
public sealed class LocalBatchRunnerBackendTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"linkuity-cli-backend-tests-{Guid.NewGuid():N}");
    private PostgreSqlContainer? _pg;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        if (!DockerProbe.IsAvailable())
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .Build();
        await _pg.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
            await _pg.DisposeAsync();

        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // ──────────────────────────────── Non-gated: file default ────────────────────────────────

    [Fact]
    public async Task FileDefault_NoMetadataStoreFlag_ProjectRoundTrips()
    {
        var metadataPath = Path.Combine(_root, "file-default", "metadata.json");
        var runner = new LocalBatchRunner(new NoOpMatchingProcess());

        // project create — no --metadata-store, so uses file path as before
        var exit = await runner.RunAsync(
        [
            "project", "create",
            "--metadata", metadataPath,
            "--name", "BackendDefaultTest",
            "--content-type", "person"
        ], CancellationToken.None);
        Assert.Equal(0, exit);

        // Read back via the store directly to confirm data persisted
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = metadataPath });
        var projects = await store.ListProjectsAsync(CancellationToken.None);
        var project = Assert.Single(projects);
        Assert.Equal("BackendDefaultTest", project.Name);

        // source create
        exit = await runner.RunAsync(
        [
            "source", "create",
            "--metadata", metadataPath,
            "--project-id", project.Id.ToString(),
            "--name", "CRM"
        ], CancellationToken.None);
        Assert.Equal(0, exit);

        // batch create
        exit = await runner.RunAsync(
        [
            "batch", "create",
            "--metadata", metadataPath,
            "--project-id", project.Id.ToString(),
            "--source-id", (await store.ListSourcesAsync(project.Id, CancellationToken.None)).Single().Id.ToString(),
            "--record-count", "5"
        ], CancellationToken.None);
        Assert.Equal(0, exit);

        var batches = await store.ListIngestBatchesAsync(project.Id, CancellationToken.None);
        var batch = Assert.Single(batches);
        Assert.Equal(5, batch.RecordCount);
    }

    // ──────────────────────────────── Gated: postgres backend ────────────────────────────────

    [SkippableFact]
    public async Task PostgresBackend_ProjectSourceBatch_RoundTrips()
    {
        Skip.IfNot(DockerProbe.IsAvailable(), "Docker not available — skipping Testcontainers test");

        var connectionString = _pg!.GetConnectionString();
        var indexDir = Path.Combine(_root, "pg-lucene-index");
        var runner = new LocalBatchRunner(new NoOpMatchingProcess());

        // ── project create ──────────────────────────────────────────────────────
        // DbUpMigrator.EnsureSchema uses LogToNowhere (silent), so stdout contains
        // only the command output. We extract the GUID via a line scan.
        var stdOut = await CaptureAsync(runner,
        [
            "project", "create",
            "--metadata-store", "postgres",
            "--connection-string", connectionString,
            "--index-dir", indexDir,
            "--name", "PgBackendTest",
            "--content-type", "person"
        ]);
        var projectId = ExtractGuid(stdOut, "project create");

        // ── source create ───────────────────────────────────────────────────────
        stdOut = await CaptureAsync(runner,
        [
            "source", "create",
            "--metadata-store", "postgres",
            "--connection-string", connectionString,
            "--index-dir", indexDir,
            "--project-id", projectId.ToString(),
            "--name", "CRM"
        ]);
        var sourceId = ExtractGuid(stdOut, "source create");

        // ── batch create ────────────────────────────────────────────────────────
        stdOut = await CaptureAsync(runner,
        [
            "batch", "create",
            "--metadata-store", "postgres",
            "--connection-string", connectionString,
            "--index-dir", indexDir,
            "--project-id", projectId.ToString(),
            "--source-id", sourceId.ToString(),
            "--record-count", "3"
        ]);
        var batchId = ExtractGuid(stdOut, "batch create");
        Assert.NotEqual(Guid.Empty, batchId);

        // ── project get — confirms the CLI reads back the stored data ───────────
        stdOut = await CaptureAsync(runner,
        [
            "project", "get",
            "--metadata-store", "postgres",
            "--connection-string", connectionString,
            "--index-dir", indexDir,
            "--project-id", projectId.ToString()
        ]);
        Assert.Contains("PgBackendTest", stdOut);
        Assert.Contains("person", stdOut);
    }

    // ──────────────────────────────── helpers ────────────────────────────────

    /// <summary>
    /// DbUpMigrator uses LogToNowhere (silent), so migration output never appears in
    /// captured stdout.  This helper finds the last line that successfully parses as a
    /// GUID — which is always the ID printed by project/source/batch create commands.
    /// </summary>
    private static Guid ExtractGuid(string output, string context)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines.Reverse())
        {
            if (Guid.TryParse(line, out var guid))
                return guid;
        }
        throw new InvalidOperationException($"No GUID line found in {context} output:\n{output}");
    }

    private static async Task<string> CaptureAsync(LocalBatchRunner runner, string[] args)
    {
        using var output = new StringWriter();
        var previousOut = Console.Out;
        Console.SetOut(output);
        try
        {
            var exit = await runner.RunAsync(args, CancellationToken.None);
            Assert.Equal(0, exit);
        }
        finally
        {
            Console.SetOut(previousOut);
        }

        return output.ToString();
    }

    private sealed class NoOpMatchingProcess : IMatchingProcess
    {
        public Task RunAsync(string artifactRoot, string jobId, CancellationToken ct) => Task.CompletedTask;
    }
}
