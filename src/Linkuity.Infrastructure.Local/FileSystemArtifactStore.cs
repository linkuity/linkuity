using System.Text.Json;
using System.Text.Json.Serialization;
using Linkuity.Core.Interfaces;

namespace Linkuity.Infrastructure.Local;

public sealed class FileSystemArtifactStore : IBlobStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private readonly string _rootPath;
    private readonly string _rootPathWithSeparator;

    public FileSystemArtifactStore(FileSystemArtifactStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.RootPath))
            throw new ArgumentException("Artifact root path is required.", nameof(options));

        _rootPath = Path.GetFullPath(options.RootPath);
        _rootPathWithSeparator = _rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;
    }

    public async Task UploadAsync(string path, Stream data, string contentType, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var fullPath = ResolvePath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using var destination = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await data.CopyToAsync(destination, ct);
    }

    public Task<Stream> DownloadAsync(string path, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(path);
        return Task.FromResult<Stream>(new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read));
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(path);
        return Task.FromResult(File.Exists(fullPath));
    }

    public async Task WriteJsonAsync<T>(string path, T value, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, ct);
    }

    public async Task<T?> ReadJsonAsync<T>(string path, CancellationToken ct = default)
    {
        if (!await ExistsAsync(path, ct))
            return default;

        await using var stream = await DownloadAsync(path, ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
    }

    private string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Artifact path is required.", nameof(path));

        var normalizedPath = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalizedPath) || Path.IsPathFullyQualified(normalizedPath))
            throw new ArgumentException("Artifact path must be relative.", nameof(path));

        var fullPath = Path.GetFullPath(Path.Combine(_rootPath, normalizedPath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullPath.Equals(_rootPath, comparison) &&
            !fullPath.StartsWith(_rootPathWithSeparator, comparison))
            throw new ArgumentException("Artifact path must stay within the artifact root.", nameof(path));

        return fullPath;
    }
}
