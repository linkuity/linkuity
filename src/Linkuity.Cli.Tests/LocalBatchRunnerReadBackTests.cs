using System.Text;
using Linkuity.Cli;
using Linkuity.Infrastructure.Local;

namespace Linkuity.Cli.Tests;

public sealed class LocalBatchRunnerReadBackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"linkuity-cli-readback-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task ClusterList_WritesMemberSourceRecordIdsToStdout()
    {
        var (runner, metadataPath, projectId) = await SeedAsync();

        var output = await CaptureAsync(runner,
        [
            "cluster", "list",
            "--metadata", metadataPath,
            "--project-id", projectId.ToString()
        ]);

        Assert.Contains("cluster_id,record_count,member_ids", output);
        Assert.Contains("crm-001|mkt-001", output);
        Assert.Contains(",2,", output);
    }

    [Fact]
    public async Task GoldenList_WritesCurrentGoldenRecordsWithVersionAndFields()
    {
        var (runner, metadataPath, projectId) = await SeedAsync();

        var output = await CaptureAsync(runner,
        [
            "golden", "list",
            "--metadata", metadataPath,
            "--project-id", projectId.ToString()
        ]);

        Assert.Contains("cluster_id,version,record_count,member_ids", output);
        Assert.Contains("email", output);
        Assert.Contains("alice@example.com", output);
        Assert.Contains("crm-001|mkt-001", output);
        Assert.Contains(",1,", output); // version number resolved via CurrentVersionId
    }

    [Fact]
    public async Task GoldenHistory_WritesVersionRowsForProject()
    {
        var (runner, metadataPath, projectId) = await SeedAsync();

        var output = await CaptureAsync(runner,
        [
            "golden", "history",
            "--metadata", metadataPath,
            "--project-id", projectId.ToString()
        ]);

        Assert.Contains("cluster_id,version,created_at", output);
        Assert.Contains("email", output);
        // The seed import creates version 1 for the single cluster.
        Assert.Contains(",1,", output);
    }

    [Fact]
    public async Task ReviewList_WritesHeaderToStdout()
    {
        var (runner, metadataPath, projectId) = await SeedAsync();

        var output = await CaptureAsync(runner,
        [
            "review", "list",
            "--metadata", metadataPath,
            "--project-id", projectId.ToString()
        ]);

        Assert.Contains("new_entity_record_id,candidate_entity_record_id,score,reason,status", output);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private async Task<(LocalBatchRunner Runner, string MetadataPath, Guid ProjectId)> SeedAsync()
    {
        var metadataPath = Path.Combine(_root, "metadata.json");
        var artifactRoot = Path.Combine(_root, "artifacts");
        var jobId = Guid.NewGuid();
        var runner = new LocalBatchRunner(new NoOpMatchingProcess());

        await runner.RunAsync(["project", "create", "--metadata", metadataPath, "--name", "Readback MDM", "--content-type", "person"], CancellationToken.None);
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = metadataPath });
        var project = Assert.Single(await store.ListProjectsAsync(CancellationToken.None));
        await runner.RunAsync(["source", "create", "--metadata", metadataPath, "--project-id", project.Id.ToString(), "--name", "Import"], CancellationToken.None);
        var source = Assert.Single(await store.ListSourcesAsync(project.Id, CancellationToken.None));
        await runner.RunAsync(["batch", "create", "--metadata", metadataPath, "--project-id", project.Id.ToString(), "--source-id", source.Id.ToString(), "--job-id", jobId.ToString(), "--record-count", "2"], CancellationToken.None);
        var batch = Assert.Single(await store.ListIngestBatchesAsync(project.Id, CancellationToken.None));

        SeedCompletedArtifacts(artifactRoot, jobId);
        await runner.RunAsync(
        [
            "persist-batch",
            "--metadata", metadataPath,
            "--artifact-root", artifactRoot,
            "--job-id", jobId.ToString(),
            "--project-id", project.Id.ToString(),
            "--source-id", source.Id.ToString(),
            "--batch-id", batch.Id.ToString()
        ], CancellationToken.None);

        return (runner, metadataPath, project.Id);
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

    private static void SeedCompletedArtifacts(string artifactRoot, Guid jobId)
    {
        var jobPath = Path.Combine(artifactRoot, jobId.ToString());
        Directory.CreateDirectory(jobPath);
        File.WriteAllText(Path.Combine(jobPath, "metadata.json"), """{"id":""" + jobId + """}""", Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(jobPath, "normalized.csv"),
            """
            id,source,name,email
            crm-001,CRM,Alice,alice@example.com
            mkt-001,Marketing,Alice M,alice@example.com
            """,
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(jobPath, "matches.csv"),
            """
            left_id,right_id,similarity,fuzzy_similarity
            crm-001,mkt-001,0.99,0.99
            """,
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(jobPath, "golden_records.csv"),
            """
            cluster_id,record_count,member_ids,email,name
            00000000-0000-0000-0000-000000000001,2,crm-001|mkt-001,alice@example.com,Alice
            """,
            Encoding.UTF8);
    }

    private sealed class NoOpMatchingProcess : IMatchingProcess
    {
        public Task RunAsync(string artifactRoot, string jobId, CancellationToken ct) => Task.CompletedTask;
    }
}
