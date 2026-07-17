using Linkuity.Core.Interfaces;

namespace Linkuity.Cli;

public sealed class LocalBlobStoreAdapter : IBlobStore
{
    private readonly IArtifactStore _artifactStore;

    public LocalBlobStoreAdapter(IArtifactStore artifactStore) => _artifactStore = artifactStore;

    public Task UploadAsync(string path, Stream data, string contentType, CancellationToken ct = default)
        => _artifactStore.UploadAsync(path, data, contentType, ct);

    public Task<Stream> DownloadAsync(string path, CancellationToken ct = default)
        => _artifactStore.DownloadAsync(path, ct);

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
        => _artifactStore.ExistsAsync(path, ct);

    public Task WriteJsonAsync<T>(string path, T value, CancellationToken ct = default)
        => _artifactStore.WriteJsonAsync(path, value, ct);

    public Task<T?> ReadJsonAsync<T>(string path, CancellationToken ct = default)
        => _artifactStore.ReadJsonAsync<T>(path, ct);
}
