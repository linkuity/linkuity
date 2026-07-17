using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Linkuity.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace Linkuity.Infrastructure.Azure;

public class AzureBlobStore : IBlobStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private readonly BlobContainerClient _container;
    private bool _initialized;

    public AzureBlobStore(IOptions<BlobStorageOptions> options)
    {
        var opts = options.Value;
        _container = new BlobContainerClient(opts.ConnectionString, opts.ContainerName);
    }

    public async Task UploadAsync(string path, Stream data, string contentType, CancellationToken ct = default)
    {
        await EnsureContainerAsync(ct);
        var blob = _container.GetBlobClient(path);
        await blob.UploadAsync(data, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
        }, ct);
    }

    public async Task<Stream> DownloadAsync(string path, CancellationToken ct = default)
    {
        await EnsureContainerAsync(ct);
        var blob = _container.GetBlobClient(path);
        var response = await blob.DownloadStreamingAsync(cancellationToken: ct);
        return response.Value.Content;
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        await EnsureContainerAsync(ct);
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
        var stream = await DownloadAsync(path, ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
    }

    private async Task EnsureContainerAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _container.CreateIfNotExistsAsync(cancellationToken: ct);
        _initialized = true;
    }
}
