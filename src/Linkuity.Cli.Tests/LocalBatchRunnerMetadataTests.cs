using System.Text;
using Linkuity.Cli;
using Linkuity.Infrastructure.Local;

namespace Linkuity.Cli.Tests;

public sealed class LocalBatchRunnerMetadataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"linkuity-cli-metadata-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task MetadataCommands_CreateProjectSourceAndBatchWithoutChangingRunCommand()
    {
        var metadataPath = Path.Combine(_root, "metadata.json");
        var runner = new LocalBatchRunner();

        Assert.Equal(0, await runner.RunAsync(["project", "create", "--metadata", metadataPath, "--name", "Customer MDM", "--content-type", "person"], CancellationToken.None));
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = metadataPath });
        var project = Assert.Single(await store.ListProjectsAsync(CancellationToken.None));

        Assert.Equal(0, await runner.RunAsync(["source", "create", "--metadata", metadataPath, "--project-id", project.Id.ToString(), "--name", "CRM"], CancellationToken.None));
        var source = Assert.Single(await store.ListSourcesAsync(project.Id, CancellationToken.None));

        Assert.Equal(0, await runner.RunAsync(["batch", "create", "--metadata", metadataPath, "--project-id", project.Id.ToString(), "--source-id", source.Id.ToString(), "--record-count", "2"], CancellationToken.None));
        var batch = Assert.Single(await store.ListIngestBatchesAsync(project.Id, CancellationToken.None));

        Assert.Equal(project.Id, batch.ProjectId);
        Assert.Equal(source.Id, batch.SourceId);
        Assert.Equal(2, batch.RecordCount);
    }

    /// <summary>
    /// LocalBatchRunner.RunMetadataCommandAsync always attaches a Lucene index to the file store
    /// for durable commands (see LocalBatchRunner.cs:196-204), so `record delete` run via the CLI
    /// always exercises FileMetadataStore's index-backed deletion path (#66). This asserts the
    /// full path end-to-end through the CLI: success exit code, the "Records deleted: N" message,
    /// and that the record no longer lists as current — the index-removal behavior itself is
    /// covered directly against FileMetadataStore in
    /// FileMetadataStoreTests.DeleteRecordsAsync_OnIndexedStore_RemovesFromIndex.
    /// </summary>
    [Fact]
    public async Task RecordDelete_ViaCliWithAttachedIndex_Succeeds()
    {
        var metadataPath = Path.Combine(_root, "metadata-record-delete-guard.json");
        var inputPath = Path.Combine(_root, "record-delete-guard.csv");
        var runner = new LocalBatchRunner();

        await runner.RunAsync(["project", "create", "--metadata", metadataPath, "--name", "Customer MDM", "--content-type", "person"], CancellationToken.None);
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = metadataPath });
        var project = Assert.Single(await store.ListProjectsAsync(CancellationToken.None));
        await runner.RunAsync(["source", "create", "--metadata", metadataPath, "--project-id", project.Id.ToString(), "--name", "CRM"], CancellationToken.None);
        var source = Assert.Single(await store.ListSourcesAsync(project.Id, CancellationToken.None));
        await runner.RunAsync(["batch", "create", "--metadata", metadataPath, "--project-id", project.Id.ToString(), "--source-id", source.Id.ToString(), "--record-count", "1"], CancellationToken.None);
        var ingestBatch = Assert.Single(await store.ListIngestBatchesAsync(project.Id, CancellationToken.None));

        await File.WriteAllTextAsync(
            inputPath,
            """
            id,source,name,email
            crm-001,CRM,Alice,alice@example.com
            """,
            System.Text.Encoding.UTF8);

        var ingestExit = await runner.RunAsync(
            [
                "ingest-incremental",
                "--metadata", metadataPath,
                "--project-id", project.Id.ToString(),
                "--source-id", source.Id.ToString(),
                "--batch-id", ingestBatch.Id.ToString(),
                "--input", inputPath,
                "--auto-threshold", "0.90",
                "--review-threshold", "0.75"
            ],
            CancellationToken.None);
        Assert.Equal(0, ingestExit);

        await runner.RunAsync(["batch", "create", "--metadata", metadataPath, "--project-id", project.Id.ToString(), "--source-id", source.Id.ToString(), "--record-count", "0"], CancellationToken.None);
        var deletionBatch = (await store.ListIngestBatchesAsync(project.Id, CancellationToken.None)).Last();

        using var standardOutput = new StringWriter();
        var previousOutput = Console.Out;
        Console.SetOut(standardOutput);
        int exitCode;
        try
        {
            exitCode = await runner.RunAsync(
                [
                    "record", "delete",
                    "--metadata", metadataPath,
                    "--project-id", project.Id.ToString(),
                    "--source-id", source.Id.ToString(),
                    "--batch-id", deletionBatch.Id.ToString(),
                    "--source-record-id", "crm-001"
                ],
                CancellationToken.None);
        }
        finally
        {
            Console.SetOut(previousOutput);
        }

        Assert.Equal(0, exitCode);
        Assert.Contains("Records deleted: 1", standardOutput.ToString());
        Assert.Empty(await store.ListEntityRecordsAsync(project.Id, CancellationToken.None));
    }

    [Fact]
    public async Task RecordDelete_MissingSourceRecordIdOption_FailsGracefully()
    {
        var metadataPath = Path.Combine(_root, "metadata-record-delete-missing-arg.json");
        var runner = new LocalBatchRunner();

        await runner.RunAsync(["project", "create", "--metadata", metadataPath, "--name", "Customer MDM", "--content-type", "person"], CancellationToken.None);
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = metadataPath });
        var project = Assert.Single(await store.ListProjectsAsync(CancellationToken.None));
        await runner.RunAsync(["source", "create", "--metadata", metadataPath, "--project-id", project.Id.ToString(), "--name", "CRM"], CancellationToken.None);
        var source = Assert.Single(await store.ListSourcesAsync(project.Id, CancellationToken.None));
        await runner.RunAsync(["batch", "create", "--metadata", metadataPath, "--project-id", project.Id.ToString(), "--source-id", source.Id.ToString(), "--record-count", "0"], CancellationToken.None);
        var batch = Assert.Single(await store.ListIngestBatchesAsync(project.Id, CancellationToken.None));

        using var errorOutput = new StringWriter();
        var previousError = Console.Error;
        Console.SetError(errorOutput);
        int exitCode;
        try
        {
            exitCode = await runner.RunAsync(
                [
                    "record", "delete",
                    "--metadata", metadataPath,
                    "--project-id", project.Id.ToString(),
                    "--source-id", source.Id.ToString(),
                    "--batch-id", batch.Id.ToString()
                ],
                CancellationToken.None);
        }
        finally
        {
            Console.SetError(previousError);
        }

        Assert.Equal(2, exitCode);
        Assert.Contains("--source-record-id", errorOutput.ToString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
