using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Linkuity.Core.Interfaces;
using Linkuity.Core.Models;
using Linkuity.Core.Normalization;
using Linkuity.Matching.Profiles;

namespace Linkuity.Pipeline;

public class CsvNormalizationService
{
    private readonly IBlobStore _blobs;

    public CsvNormalizationService(IBlobStore blobs) => _blobs = blobs;

    public Task<int> NormalizeAsync(Guid jobId, MatchConfiguration config, CancellationToken ct = default)
    {
        var fieldMap = config.Fields.ToDictionary(f => f.Name, f => f.SemanticType, StringComparer.OrdinalIgnoreCase);
        return NormalizeAsync(jobId, fieldMap, ct);
    }

    public Task<int> NormalizeAsync(Guid jobId, MatchingProfile profile, CancellationToken ct = default)
    {
        var fieldMap = profile.Fields.ToDictionary(f => f.Name, f => f.SemanticType, StringComparer.OrdinalIgnoreCase);
        return NormalizeAsync(jobId, fieldMap, ct);
    }

    private async Task<int> NormalizeAsync(Guid jobId, IReadOnlyDictionary<string, SemanticFieldType> fieldMap, CancellationToken ct)
    {
        using var inputStream = await _blobs.DownloadAsync($"{jobId}/input.csv", ct);
        using var reader = new StreamReader(inputStream);
        var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture);
        using var csvReader = new CsvReader(reader, csvConfig);

        using var output = new MemoryStream();
        using var writer = new StreamWriter(output, leaveOpen: true);
        using var csvWriter = new CsvWriter(writer, csvConfig);

        await csvReader.ReadAsync();
        csvReader.ReadHeader();
        var headers = csvReader.HeaderRecord!;

        foreach (var header in headers)
            csvWriter.WriteField(header);
        csvWriter.NextRecord();

        var rowCount = 0;
        while (await csvReader.ReadAsync())
        {
            rowCount++;
            foreach (var header in headers)
            {
                var value = csvReader.GetField(header) ?? string.Empty;
                if (fieldMap.TryGetValue(header, out var fieldType))
                    value = FieldNormalizer.Normalize(value, fieldType);
                csvWriter.WriteField(value);
            }
            csvWriter.NextRecord();
        }

        await writer.FlushAsync(ct);
        output.Position = 0;
        await _blobs.UploadAsync($"{jobId}/normalized.csv", output, "text/csv", ct);
        return rowCount;
    }
}
