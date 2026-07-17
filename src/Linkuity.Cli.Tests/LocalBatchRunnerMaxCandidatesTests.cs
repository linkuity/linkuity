using System.Threading;
using Linkuity.Cli;
using Linkuity.Core.Interfaces;
using Xunit;

namespace Linkuity.Cli.Tests;

public sealed class LocalBatchRunnerMaxCandidatesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "linkuity-m19-maxcand-" + Guid.NewGuid().ToString("N"));

    public LocalBatchRunnerMaxCandidatesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class NoOpMatchingProcess : IMatchingProcess
    {
        public Task RunAsync(string artifactRoot, string jobId, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task IngestIncremental_WithNonNumericMaxCandidates_ExitsTwo()
    {
        var runner = new LocalBatchRunner(new NoOpMatchingProcess());
        var metadataPath = Path.Combine(_dir, "meta.json");

        Assert.Equal(0, await runner.RunAsync(
            ["project", "create", "--metadata", metadataPath, "--name", "P", "--content-type", "person"],
            CancellationToken.None));

        // A non-numeric --max-candidates must be rejected with exit code 2, not crash.
        var exit = await runner.RunAsync(
            ["golden", "list", "--metadata", metadataPath, "--project-id", Guid.NewGuid().ToString(), "--max-candidates", "abc"],
            CancellationToken.None);

        Assert.Equal(2, exit);
    }
}
