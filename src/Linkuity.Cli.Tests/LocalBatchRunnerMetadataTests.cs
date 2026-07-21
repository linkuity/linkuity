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

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
