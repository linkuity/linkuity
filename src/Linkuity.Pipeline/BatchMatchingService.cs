using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Linkuity.Core.Interfaces;
using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;

namespace Linkuity.Pipeline;

/// <summary>
/// Native replacement for the Python batch matcher. Reads a job's normalized records,
/// reuses the matching engine to produce within-batch pairwise edges, and writes
/// matches.csv. Downstream post-processing turns those edges into clusters and golden
/// records; it reads only left_id/right_id and applies no threshold, so the match cut
/// lives here.
/// </summary>
public sealed class BatchMatchingService
{
    private readonly IArtifactStore _store;

    public BatchMatchingService(IArtifactStore store)
    {
        _store = store;
    }

    public async Task RunAsync(string jobId, MatchingProfile profile, CancellationToken ct)
    {
        List<(string SourceId, IReadOnlyDictionary<string, string> Fields)> rows;
        await using (var normalized = await _store.DownloadAsync($"{jobId}/normalized.csv", ct))
            rows = ReadRows(normalized);

        var matchesCsv = BuildMatchesCsv(rows, profile);

        using var outStream = new MemoryStream(Encoding.UTF8.GetBytes(matchesCsv));
        await _store.UploadAsync($"{jobId}/matches.csv", outStream, "text/csv", ct);
    }

    public static string BuildMatchesCsv(
        IReadOnlyList<(string SourceId, IReadOnlyDictionary<string, string> Fields)> rows,
        MatchingProfile profile)
    {
        profile = profile.WithCandidateRetrievalStrategy("blocking-linear");
        var engine = new MatchingEngine(MatchingDefaults.CreateRegistry());
        var now = DateTimeOffset.UtcNow;

        var records = new List<EntityRecord>(rows.Count);
        foreach (var (sourceId, fields) in rows)
        {
            var seed = NewRecord(sourceId, fields, now, []);
            var keys = engine.GenerateBlockingKeys(seed, profile);
            records.Add(NewRecord(sourceId, fields, now, keys));
        }

        var cut = profile.AutoMatchThreshold;
        var best = new Dictionary<(string, string), double>();
        foreach (var record in records)
        {
            var others = records.Where(r => r.Id != record.Id).ToList();
            if (others.Count == 0) continue;

            var result = engine.Resolve(record, others, profile);
            foreach (var candidate in result.Candidates)
            {
                if (candidate.Score < cut) continue;
                var a = record.SourceRecordId;
                var b = candidate.Record.SourceRecordId;
                if (string.Equals(a, b, StringComparison.Ordinal)) continue;

                var key = string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
                if (!best.TryGetValue(key, out var existing) || candidate.Score > existing)
                    best[key] = candidate.Score;
            }
        }

        var writer = new StringWriter();
        using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
        {
            csv.WriteField("left_id");
            csv.WriteField("right_id");
            csv.WriteField("similarity");
            csv.WriteField("fuzzy_similarity");
            csv.NextRecord();

            foreach (var pair in best
                .OrderBy(p => p.Key.Item1, StringComparer.Ordinal)
                .ThenBy(p => p.Key.Item2, StringComparer.Ordinal))
            {
                csv.WriteField(pair.Key.Item1);
                csv.WriteField(pair.Key.Item2);
                csv.WriteField(pair.Value.ToString(CultureInfo.InvariantCulture));
                csv.WriteField(""); // fuzzy_similarity: display-only in Neo4j export, optional
                csv.NextRecord();
            }
        }
        return writer.ToString();
    }

    private static EntityRecord NewRecord(
        string sourceId, IReadOnlyDictionary<string, string> fields,
        DateTimeOffset now, IReadOnlyList<string> blockingKeys) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = Guid.Empty,
        SourceId = Guid.Empty,
        IngestBatchId = Guid.Empty,
        SourceRecordId = sourceId,
        Fields = fields,
        BlockingKeys = blockingKeys,
        CreatedAt = now
    };

    private static List<(string, IReadOnlyDictionary<string, string>)> ReadRows(Stream stream)
    {
        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture));
        var rows = new List<(string, IReadOnlyDictionary<string, string>)>();
        if (!csv.Read()) return rows;
        csv.ReadHeader();
        var headers = csv.HeaderRecord ?? [];
        while (csv.Read())
        {
            var fields = headers.ToDictionary(h => h, h => csv.GetField(h) ?? "", StringComparer.OrdinalIgnoreCase);
            if (fields.TryGetValue("id", out var id) && !string.IsNullOrEmpty(id))
                rows.Add((id, fields));
        }
        return rows;
    }
}
