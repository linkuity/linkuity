using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Linkuity.Pipeline;

namespace Linkuity.Cli;

/// <summary>
/// Flag parsing and record-source resolution shared by `match blocking` and
/// `match scoring`. Extracted verbatim from BlockingAuditCommands; behavior-neutral.
/// </summary>
internal static class AuditCliCommon
{
    // ---- minimal flag parser: "--name value" pairs ----

    internal static Dictionary<string, string> ParseFlags(IEnumerable<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? pending = null;
        foreach (var arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (pending is not null) options[pending] = "true";
                pending = arg[2..];
            }
            else if (pending is not null)
            {
                options[pending] = arg;
                pending = null;
            }
        }
        if (pending is not null) options[pending] = "true";
        return options;
    }

    // ---- Source resolution: CSV, File-store, or Postgres-store ----

    internal static async Task<IReadOnlyList<EntityRecord>> LoadRecordsAsync(
        IReadOnlyDictionary<string, string> options, CancellationToken ct)
    {
        var hasCsv = options.TryGetValue("input", out var csvPath) && !string.IsNullOrWhiteSpace(csvPath);
        var hasMetadata = options.TryGetValue("metadata", out var metaPath) && !string.IsNullOrWhiteSpace(metaPath);
        var isPostgres = options.TryGetValue("metadata-store", out var storeType)
                         && string.Equals(storeType, "postgres", StringComparison.OrdinalIgnoreCase);

        var sourceCount = (hasCsv ? 1 : 0) + (hasMetadata ? 1 : 0) + (isPostgres ? 1 : 0);
        if (sourceCount != 1)
            throw new ArgumentException(
                "Exactly one record source is required: --input <csv>, --metadata <path>, " +
                "or --metadata-store postgres --connection-string <cs> (all store sources need --project-id).");

        if (hasCsv)
        {
            if (!File.Exists(csvPath))
                throw new FileNotFoundException($"Input CSV not found: {csvPath}", csvPath);
            return ReadCsv(csvPath!);
        }

        var projectId = ParseProjectId(options);

        if (hasMetadata)
        {
            var store = new Linkuity.Infrastructure.Local.FileMetadataStore(
                new Linkuity.Infrastructure.Local.FileMetadataStoreOptions { DatabasePath = metaPath! });
            return await store.ListEntityRecordsAsync(projectId, ct);
        }

        // Postgres
        if (!options.TryGetValue("connection-string", out var cs) || string.IsNullOrWhiteSpace(cs))
            throw new ArgumentException("Postgres source requires --connection-string.");
        Linkuity.Infrastructure.Postgres.DbUpMigrator.EnsureSchema(cs);
        var pg = new Linkuity.Infrastructure.Postgres.PostgresMetadataStore(
            new Linkuity.Infrastructure.Postgres.PostgresMetadataStoreOptions { ConnectionString = cs },
            engine: null, profileProvider: null, indexedRetrieval: null);
        return await pg.ListEntityRecordsAsync(projectId, ct);
    }

    internal static Guid ParseProjectId(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("project-id", out var raw) || !Guid.TryParse(raw, out var id))
            throw new ArgumentException("A valid --project-id <guid> is required for store sources.");
        return id;
    }

    internal static IReadOnlyList<EntityRecord> ReadCsv(string path)
    {
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture));
        var records = new List<EntityRecord>();
        if (!csv.Read()) return records;
        csv.ReadHeader();
        var headers = csv.HeaderRecord ?? [];
        var now = DateTimeOffset.UnixEpoch;
        while (csv.Read())
        {
            var fields = headers.ToDictionary(h => h, h => csv.GetField(h) ?? "", StringComparer.OrdinalIgnoreCase);
            if (!fields.TryGetValue("id", out var id) || string.IsNullOrEmpty(id))
                continue;
            records.Add(new EntityRecord
            {
                Id = Guid.NewGuid(), ProjectId = Guid.Empty, SourceId = Guid.Empty, IngestBatchId = Guid.Empty,
                SourceRecordId = id, Fields = fields, CreatedAt = now
            });
        }
        return records;
    }

    /// <summary>
    /// THE reader for the (record_id, canonical_key) ground-truth format (spec §4): duplicate
    /// record_id rows fail fast rather than last-write-wins. Shared by `match scoring audit` and
    /// `match corpus audit` — two copies would let the two instruments silently disagree about the
    /// same file if either drifted. The lenient variant in BlockingAuditCommands is deliberately
    /// separate; it tolerates duplicates.
    /// </summary>
    internal static Dictionary<string, string> ReadGroundTruthStrict(string path)
    {
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture));
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!csv.Read()) return map;
        csv.ReadHeader();
        while (csv.Read())
        {
            var id = csv.GetField("record_id");
            var canonical = csv.GetField("canonical_key");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(canonical)) continue;
            if (!map.TryAdd(id, canonical))
                throw new ArgumentException($"Duplicate record_id in ground truth: '{id}'.");
        }
        return map;
    }

    /// <summary>--max-block-size flag > profile maxBlockSize > off. Null Error means valid.</summary>
    internal static (int? Value, string? Error) ResolveMaxBlockSize(
        IReadOnlyDictionary<string, string> options, MatchingProfile profile)
    {
        if (options.TryGetValue("max-block-size", out var raw))
        {
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 1)
                return (null, $"Invalid --max-block-size value: {raw}");
            return (value, null);
        }
        return (profile.MaxBlockSize, null);
    }

    // ---- --eval-only: restrict a loaded record set to the held-out half of the SAME split ----
    // ---- `match corpus calibrate` uses, so a downstream evaluation can honestly claim it never ----
    // ---- touched the records the evidence parameters were fit from. ----

    /// <summary>
    /// Filters <paramref name="records"/> down to the eval half of
    /// <see cref="FieldEvidenceCalibrationService.IsFitHalf"/>'s split when <c>--eval-only</c> is
    /// present, using the EXACT SAME function `match corpus calibrate` uses to build its fit half
    /// — never a second, independently written hash split, which could silently diverge from it
    /// and let "evaluated on held-out data" become a false claim nobody notices. Absent
    /// <c>--eval-only</c>, every record is kept (today's behavior, unchanged) — but a
    /// <c>--fit-fraction</c> given without it is refused rather than silently ignored, since a
    /// flag that looks like it does something and does not is worse than one this command
    /// rejects outright.
    /// <para>
    /// Shared by every corpus-scale audit command (`match corpus audit`, `match corpus ablate`,
    /// `match scoring audit`) rather than duplicated in each: a threshold sweep and the
    /// before/after comparison it feeds both depend on every command in the comparison having
    /// filtered the SAME way, off the SAME split function.
    /// </para>
    /// </summary>
    internal static (IReadOnlyList<EntityRecord> Records, string? Error) ApplyEvalOnlyFilter(
        IReadOnlyDictionary<string, string> options, IReadOnlyList<EntityRecord> records)
    {
        var hasFitFraction = options.TryGetValue("fit-fraction", out var fitRaw);
        if (!options.ContainsKey("eval-only"))
            return hasFitFraction
                ? (records, "`--fit-fraction` has no effect without `--eval-only`.")
                : (records, null);

        var fitFraction = 0.5;
        if (hasFitFraction &&
            (!double.TryParse(fitRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out fitFraction) ||
             fitFraction <= 0 || fitFraction >= 1))
            return (records, $"Invalid --fit-fraction value: {fitRaw}");

        var evalHalf = records
            .Where(r => !FieldEvidenceCalibrationService.IsFitHalf(r.SourceRecordId, fitFraction))
            .ToList();
        return (evalHalf, null);
    }
}
