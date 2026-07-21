using System.Threading;
using Linkuity.Cli;
using Linkuity.Core.Interfaces;
using Xunit;

namespace Linkuity.Cli.Tests;

public sealed class LocalBatchRunnerIngestParallelismTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "linkuity-ingestpar-" + Guid.NewGuid().ToString("N"));

    public LocalBatchRunnerIngestParallelismTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Metadata_WithNonNumericIngestParallelism_ExitsTwo()
    {
        var runner = new LocalBatchRunner();
        var metadataPath = Path.Combine(_dir, "meta.json");

        Assert.Equal(0, await runner.RunAsync(
            ["project", "create", "--metadata", metadataPath, "--name", "P", "--content-type", "person"],
            CancellationToken.None));

        var exit = await runner.RunAsync(
            ["golden", "list", "--metadata", metadataPath, "--project-id", Guid.NewGuid().ToString(), "--ingest-parallelism", "abc"],
            CancellationToken.None);

        Assert.Equal(2, exit);
    }
}
