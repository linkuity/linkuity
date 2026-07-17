namespace Linkuity.Core.Interfaces;

public interface IArtifactStore
{
    Task UploadAsync(string path, Stream data, string contentType, CancellationToken ct = default);
    Task<Stream> DownloadAsync(string path, CancellationToken ct = default);
    Task<bool> ExistsAsync(string path, CancellationToken ct = default);
    Task WriteJsonAsync<T>(string path, T value, CancellationToken ct = default);
    Task<T?> ReadJsonAsync<T>(string path, CancellationToken ct = default);
}
