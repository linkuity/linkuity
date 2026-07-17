using System.Text.Json;
using System.Text.Json.Serialization;
using Linkuity.Core.Interfaces;

namespace Linkuity.Api.Tests.TestDoubles;

public class InMemoryBlobStore : IBlobStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private readonly Dictionary<string, byte[]> _store = new();

    public Task UploadAsync(string path, Stream data, string contentType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        data.CopyTo(ms);
        _store[path] = ms.ToArray();
        return Task.CompletedTask;
    }

    public Task<Stream> DownloadAsync(string path, CancellationToken ct = default)
    {
        if (!_store.TryGetValue(path, out var bytes))
            throw new InvalidOperationException($"Blob not found: {path}");
        return Task.FromResult<Stream>(new MemoryStream(bytes));
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
        => Task.FromResult(_store.ContainsKey(path));

    public Task WriteJsonAsync<T>(string path, T value, CancellationToken ct = default)
    {
        _store[path] = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        return Task.CompletedTask;
    }

    public Task<T?> ReadJsonAsync<T>(string path, CancellationToken ct = default)
    {
        if (!_store.TryGetValue(path, out var bytes))
            return Task.FromResult<T?>(default);
        return Task.FromResult(JsonSerializer.Deserialize<T>(bytes, JsonOptions));
    }
}
