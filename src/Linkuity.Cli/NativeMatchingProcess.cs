using Linkuity.Infrastructure.Local;
using Linkuity.Pipeline;

namespace Linkuity.Cli;

public sealed class NativeMatchingProcess : IMatchingProcess
{
    public Task RunAsync(string artifactRoot, string jobId, CancellationToken ct)
    {
        var store = new FileSystemArtifactStore(new FileSystemArtifactStoreOptions { RootPath = artifactRoot });
        return new BatchMatchingService(store).RunAsync(jobId, ct);
    }
}
