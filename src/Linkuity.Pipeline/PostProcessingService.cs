using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Linkuity.Core.Interfaces;
using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Microsoft.Extensions.Logging;

namespace Linkuity.Pipeline;

public class PostProcessingService
{
    private readonly IArtifactStore _artifactStore;
    private readonly GraphService _graphService;
    private readonly GoldenRecordService _goldenRecordService;
    private readonly ILogger<PostProcessingService> _logger;

    public PostProcessingService(
        IArtifactStore artifactStore,
        GraphService graphService,
        GoldenRecordService goldenRecordService,
        ILogger<PostProcessingService> logger)
    {
        _artifactStore = artifactStore;
        _graphService = graphService;
        _goldenRecordService = goldenRecordService;
        _logger = logger;
    }

    public async Task ProcessAsync(string jobId, CancellationToken ct = default)
    {
        var job = await _artifactStore.ReadJsonAsync<Job>($"{jobId}/metadata.json", ct)
            ?? throw new InvalidOperationException($"metadata.json for job {jobId} is empty");
        var sourceField = job.Configuration?.Fields
            .FirstOrDefault(f => f.SemanticType == SemanticFieldType.SourceIdentifier)?.Name;
        await ProcessCoreAsync(jobId, job.MergeConfiguration, sourceField, ct);
    }

    public Task ProcessAsync(string jobId, MatchingProfile profile, MergeConfiguration? merge, CancellationToken ct = default)
    {
        var sourceField = profile.Fields
            .FirstOrDefault(f => f.SemanticType == SemanticFieldType.SourceIdentifier)?.Name;
        return ProcessCoreAsync(jobId, merge, sourceField, ct);
    }

    private async Task ProcessCoreAsync(string jobId, MergeConfiguration? merge, string? sourceField, CancellationToken ct)
    {
        var metadataPath = $"{jobId}/metadata.json";
        var job = await _artifactStore.ReadJsonAsync<Job>(metadataPath, ct)
            ?? throw new InvalidOperationException($"metadata.json for job {jobId} is empty");

        try
        {
            await using var normalizedStream = await _artifactStore.DownloadAsync($"{jobId}/normalized.csv", ct);
            var recordsById = ReadCsvById(normalizedStream);

            await using var matchesStream = await _artifactStore.DownloadAsync($"{jobId}/matches.csv", ct);
            var pairs = ReadMatchPairs(matchesStream);

            var clusters = _graphService.FindClusters(recordsById.Keys, pairs);
            var goldenRecords = _goldenRecordService.Merge(clusters, recordsById, merge, sourceField);

            var csvBytes = SerializeGoldenRecords(goldenRecords);
            await using var csvStream = new MemoryStream(csvBytes);
            await _artifactStore.UploadAsync($"{jobId}/golden_records.csv", csvStream, "text/csv", ct);

            job.State = JobState.Complete;
            await _artifactStore.WriteJsonAsync(metadataPath, job, ct);
        }
        catch
        {
            job.State = JobState.Failed;
            try
            {
                await _artifactStore.WriteJsonAsync(metadataPath, job, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write Failed state for job {JobId}", jobId);
            }
            throw;
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ReadCsvById(Stream stream)
    {
        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture));
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        csv.Read();
        csv.ReadHeader();
        while (csv.Read())
        {
            var record = csv.HeaderRecord!.ToDictionary(h => h, h => csv.GetField(h) ?? "");
            if (record.TryGetValue("id", out var id) && !string.IsNullOrEmpty(id))
                result[id] = record;
        }
        return result;
    }

    private static IReadOnlyList<(string Left, string Right)> ReadMatchPairs(Stream stream)
    {
        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture));
        var pairs = new List<(string, string)>();
        if (!csv.Read()) return pairs;
        csv.ReadHeader();
        while (csv.Read())
        {
            var left = csv.GetField("left_id");
            var right = csv.GetField("right_id");
            if (!string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right))
                pairs.Add((left, right));
        }
        return pairs;
    }

    private static byte[] SerializeGoldenRecords(IReadOnlyList<GoldenRecord> records)
    {
        if (records.Count == 0) return Encoding.UTF8.GetBytes("");

        var allFields = records
            .SelectMany(r => r.Fields.Keys)
            .Distinct()
            .Order()
            .ToList();

        using var writer = new StringWriter();
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);

        csv.WriteField("cluster_id");
        csv.WriteField("record_count");
        csv.WriteField("member_ids");
        foreach (var field in allFields) csv.WriteField(field);
        csv.NextRecord();

        foreach (var record in records)
        {
            csv.WriteField(record.ClusterId.ToString());
            csv.WriteField(record.MemberIds.Count);
            csv.WriteField(string.Join("|", record.MemberIds));
            foreach (var field in allFields)
                csv.WriteField(record.Fields.TryGetValue(field, out var v) ? v : "");
            csv.NextRecord();
        }

        return Encoding.UTF8.GetBytes(writer.ToString());
    }
}
