using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Profiles.Configuration;
using Linkuity.Pipeline;

namespace Linkuity.Cli;

/// <summary>
/// `match blocking audit` and `match blocking explain`: read records from a CSV or a durable
/// store, recompute blocking keys under a profile, and report blocking quality. See
/// docs/superpowers/specs/2026-07-24-blocking-audit-instrument-design.md.
/// </summary>
public static class BlockingAuditCommands
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length < 3)
        {
            await Console.Error.WriteLineAsync("Usage: match blocking <audit|explain> [options].");
            return 2;
        }

        var verb = args[2];
        var options = ParseFlags(args.Skip(3));

        if (!options.TryGetValue("profile", out var profilePath) || string.IsNullOrWhiteSpace(profilePath))
        {
            await Console.Error.WriteLineAsync("A --profile is required.");
            return 2;
        }

        MatchingProfile profile;
        try { profile = ProfileResolver.ResolveNameOrFile(profilePath); }
        catch (Exception ex) when (ex is MatchingProfileConfigException or ArgumentException)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 2;
        }

        IReadOnlyList<EntityRecord> records;
        try { records = await LoadRecordsAsync(options, ct); }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or InvalidOperationException)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 2;
        }

        var service = new BlockingAuditService(MatchingDefaults.CreateRegistry());

        return verb.ToLowerInvariant() switch
        {
            "audit" => await AuditAsync(service, records, profile, options, ct),
            "explain" => Explain(service, records, profile, options),
            _ => await UnknownVerbAsync(verb)
        };
    }

    private static async Task<int> UnknownVerbAsync(string verb)
    {
        await Console.Error.WriteLineAsync($"Unknown blocking verb '{verb}'. Expected 'audit' or 'explain'.");
        return 2;
    }

    // ---- Source resolution: CSV, File-store, or Postgres-store ----

    private static async Task<IReadOnlyList<EntityRecord>> LoadRecordsAsync(
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

    private static Guid ParseProjectId(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("project-id", out var raw) || !Guid.TryParse(raw, out var id))
            throw new ArgumentException("A valid --project-id <guid> is required for store sources.");
        return id;
    }

    private static IReadOnlyList<EntityRecord> ReadCsv(string path)
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

    // ---- audit ----

    private static async Task<int> AuditAsync(
        BlockingAuditService service, IReadOnlyList<EntityRecord> records, MatchingProfile profile,
        IReadOnlyDictionary<string, string> options, CancellationToken ct)
    {
        IReadOnlyDictionary<string, string>? groundTruth = null;
        if (options.TryGetValue("ground-truth", out var gtPath) && !string.IsNullOrWhiteSpace(gtPath))
        {
            if (!File.Exists(gtPath))
            {
                await Console.Error.WriteLineAsync($"Ground-truth CSV not found: {gtPath}");
                return 2;
            }
            groundTruth = ReadGroundTruth(gtPath);
        }

        int? maxCandidates = options.TryGetValue("max-candidates", out var mc) && int.TryParse(mc, out var mcVal) ? mcVal : null;

        var result = service.Audit(records, profile, groundTruth, maxCandidates);
        Console.Write(BlockingAuditTextFormatter.Format(result));
        return 0;
    }

    private static IReadOnlyDictionary<string, string> ReadGroundTruth(string path)
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
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(canonical))
                map[id] = canonical;
        }
        return map;
    }

    // ---- explain ----

    private static int Explain(
        BlockingAuditService service, IReadOnlyList<EntityRecord> records, MatchingProfile profile,
        IReadOnlyDictionary<string, string> options)
    {
        var result = service.Audit(records, profile);
        var byId = result.PerRecord.ToDictionary(r => r.SourceRecordId, StringComparer.Ordinal);

        if (options.TryGetValue("left", out var left) && options.TryGetValue("right", out var right))
        {
            if (!byId.TryGetValue(left, out var l)) { Console.Error.WriteLine($"Unknown record: {left}"); return 2; }
            if (!byId.TryGetValue(right, out var r)) { Console.Error.WriteLine($"Unknown record: {right}"); return 2; }

            var shared = l.AllKeys.Intersect(r.AllKeys, StringComparer.OrdinalIgnoreCase).ToList();
            Console.WriteLine(BlockingAuditTextFormatter.FormatRecord(l));
            Console.WriteLine(BlockingAuditTextFormatter.FormatRecord(r));
            Console.WriteLine(shared.Count > 0
                ? $"WOULD COMPARE (shares {string.Join(", ", shared)})"
                : "SKIPPED (no shared key)");
            return 0;
        }

        if (options.TryGetValue("record", out var recordId))
        {
            if (!byId.TryGetValue(recordId, out var rec)) { Console.Error.WriteLine($"Unknown record: {recordId}"); return 2; }
            Console.WriteLine(BlockingAuditTextFormatter.FormatRecord(rec));
            return 0;
        }

        Console.Error.WriteLine("Provide --record <id>, or --left <id> --right <id>.");
        return 2;
    }

    // ---- minimal flag parser: "--name value" pairs ----

    private static Dictionary<string, string> ParseFlags(IEnumerable<string> args)
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
}
