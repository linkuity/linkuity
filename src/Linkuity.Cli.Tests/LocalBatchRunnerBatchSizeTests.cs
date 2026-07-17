using System.Threading;
using Linkuity.Cli;
using Linkuity.Core.Interfaces;
using Linkuity.Infrastructure.Local;
using Xunit;

namespace Linkuity.Cli.Tests;

public sealed class LocalBatchRunnerBatchSizeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "linkuity-m19-batchsize-" + Guid.NewGuid().ToString("N"));

    public LocalBatchRunnerBatchSizeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { } }

    private sealed class NoOpMatchingProcess : IMatchingProcess
    {
        public Task RunAsync(string artifactRoot, string jobId, CancellationToken ct) => Task.CompletedTask;
    }

    // Six rows: two duplicate pairs by email plus two singletons.
    // Row order is deliberately interleaved so each duplicate pair (r1/r2, r3/r4)
    // straddles a chunk boundary under --batch-size 2 (chunks: [r1,r3] [r2,r4] [r5,r6]).
    // This forces the second chunk's matches to resolve against the FIRST chunk's
    // persisted corpus rather than within a single batch, so the equivalence test
    // actually exercises cross-chunk resolution.
    private string WriteInputCsv()
    {
        var path = Path.Combine(_dir, "input.csv");
        File.WriteAllText(path,
            "id,name,email,phone\n" +
            "r1,Alice Smith,alice@example.com,111\n" +
            "r3,Bob Jones,bob@example.com,222\n" +
            "r2,Alice Smith,alice@example.com,111\n" +
            "r4,Bob Jones,bob@example.com,222\n" +
            "r5,Carol Lee,carol@example.com,333\n" +
            "r6,Dave Poe,dave@example.com,444\n");
        return path;
    }

    private async Task<(Guid projectId, Guid sourceId)> SeedProjectAsync(LocalBatchRunner runner, string metadataPath)
    {
        Assert.Equal(0, await runner.RunAsync(
            ["project", "create", "--metadata", metadataPath, "--name", "P", "--content-type", "person"],
            CancellationToken.None));
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = metadataPath });
        var project = Assert.Single(await store.ListProjectsAsync(CancellationToken.None));
        var source = await store.CreateSourceAsync(project.Id, "CRM", DateTimeOffset.UtcNow, CancellationToken.None);
        return (project.Id, source.Id);
    }

    [Fact]
    public async Task ChunkedIngest_WithoutBatchId_Succeeds_AndYieldsSameClustersAsSingleBatch()
    {
        var input = WriteInputCsv();

        // --- Single-batch ingest (reference) ---
        var runnerA = new LocalBatchRunner(new NoOpMatchingProcess());
        var metaA = Path.Combine(_dir, "single.json");
        var (projA, srcA) = await SeedProjectAsync(runnerA, metaA);
        var storeA = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = metaA });
        var batchA = await storeA.CreateIngestBatchAsync(projA, srcA, null, 6, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(0, await runnerA.RunAsync(
            ["ingest-incremental", "--metadata", metaA, "--project-id", projA.ToString(), "--source-id", srcA.ToString(),
             "--batch-id", batchA.Id.ToString(), "--input", input],
            CancellationToken.None));

        // --- Chunked ingest (batch-size 2, no --batch-id) ---
        var runnerB = new LocalBatchRunner(new NoOpMatchingProcess());
        var metaB = Path.Combine(_dir, "chunked.json");
        var (projB, srcB) = await SeedProjectAsync(runnerB, metaB);
        Assert.Equal(0, await runnerB.RunAsync(
            ["ingest-incremental", "--metadata", metaB, "--project-id", projB.ToString(), "--source-id", srcB.ToString(),
             "--input", input, "--batch-size", "2"],
            CancellationToken.None));

        // Same number of active clusters (2 pairs merge → 4 clusters) in both.
        var clustersA = await new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = metaA })
            .ListClustersAsync(projA, CancellationToken.None);
        var clustersB = await new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = metaB })
            .ListClustersAsync(projB, CancellationToken.None);

        Assert.Equal(clustersA.Count, clustersB.Count);
        Assert.Equal(4, clustersB.Count);
    }

    [Fact]
    public async Task DefaultIngest_WithoutBatchSize_StillRequiresBatchId()
    {
        var input = WriteInputCsv();
        var runner = new LocalBatchRunner(new NoOpMatchingProcess());
        var meta = Path.Combine(_dir, "default.json");
        var (proj, src) = await SeedProjectAsync(runner, meta);

        // No --batch-size and no --batch-id → the existing Required(batch-id) guard returns exit 2.
        var exit = await runner.RunAsync(
            ["ingest-incremental", "--metadata", meta, "--project-id", proj.ToString(), "--source-id", src.ToString(), "--input", input],
            CancellationToken.None);

        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task ChunkedIngest_WithInvalidBatchSize_ExitsTwo()
    {
        var input = WriteInputCsv();
        var runner = new LocalBatchRunner(new NoOpMatchingProcess());
        var meta = Path.Combine(_dir, "invalid-bs.json");
        var (proj, src) = await SeedProjectAsync(runner, meta);

        var exit = await runner.RunAsync(
            ["ingest-incremental", "--metadata", meta, "--project-id", proj.ToString(), "--source-id", src.ToString(),
             "--input", input, "--batch-size", "0"],
            CancellationToken.None);

        Assert.Equal(2, exit);
    }
}
