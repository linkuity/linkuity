using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Linkuity.Core.Interfaces;

namespace Linkuity.Infrastructure.Azure;

public sealed class AzureBlobArtifactStore : IArtifactStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private readonly BlobContainerClient _container;

    public AzureBlobArtifactStore(BlobServiceClient blobClient, string containerName)
    {
        _container = blobClient.GetBlobContainerClient(containerName);
    }

    public async Task UploadAsync(string path, Stream data, string contentType, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient(path);
        await blob.DeleteIfExistsAsync(cancellationToken: ct);
        await blob.UploadAsync(data, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
        }, ct);
    }

    public async Task<Stream> DownloadAsync(string path, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient(path);
        var response = await blob.DownloadStreamingAsync(cancellationToken: ct);
        return response.Value.Content;
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient(path);
        return await blob.ExistsAsync(ct);
    }

    public async Task WriteJsonAsync<T>(string path, T value, CancellationToken ct = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        using var stream = new MemoryStream(bytes);
        await UploadAsync(path, stream, "application/json", ct);
    }

    public async Task<T?> ReadJsonAsync<T>(string path, CancellationToken ct = default)
    {
        if (!await ExistsAsync(path, ct))
            return default;

        await using var stream = await DownloadAsync(path, ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
    }
}
