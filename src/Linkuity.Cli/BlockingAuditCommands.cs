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
        var options = AuditCliCommon.ParseFlags(args.Skip(3));

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
        try { records = await AuditCliCommon.LoadRecordsAsync(options, ct); }
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

        var (maxBlockSize, maxBlockSizeError) = AuditCliCommon.ResolveMaxBlockSize(options, profile);
        if (maxBlockSizeError is not null)
        {
            await Console.Error.WriteLineAsync(maxBlockSizeError);
            return 2;
        }

        var result = service.Audit(records, profile, groundTruth, maxCandidates, maxBlockSize);

        var format = options.TryGetValue("format", out var fmt) ? fmt : "text";
        Console.Write(string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase)
            ? BlockingAuditCsvFormatter.Format(result)
            : BlockingAuditTextFormatter.Format(result));

        if (options.TryGetValue("min-recall", out var minRaw))
        {
            if (!double.TryParse(minRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var min)
                || !double.IsFinite(min))
            {
                await Console.Error.WriteLineAsync($"Invalid --min-recall value: {minRaw}");
                return 2;
            }
            var gating = result.Suppression?.EffectiveReachability ?? result.Reachability;
            if (gating is null)
            {
                await Console.Error.WriteLineAsync("--min-recall requires --ground-truth.");
                return 2;
            }
            if (gating.Recall < min)
            {
                await Console.Error.WriteLineAsync(
                    string.Format(CultureInfo.InvariantCulture,
                        "Recall {0:P1} is below the required minimum {1:P1}.", gating.Recall, min));
                return 1;
            }
        }

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
        var (maxBlockSize, maxBlockSizeError) = AuditCliCommon.ResolveMaxBlockSize(options, profile);
        if (maxBlockSizeError is not null) { Console.Error.WriteLine(maxBlockSizeError); return 2; }

        var result = service.Audit(records, profile, groundTruth: null, maxCandidates: null, maxBlockSize);
        var byId = result.PerRecord.ToDictionary(r => r.SourceRecordId, StringComparer.Ordinal);
        var suppressedSizes = (result.Suppression?.SuppressedBlocks ?? [])
            .ToDictionary(b => b.Key, b => b.Size, StringComparer.OrdinalIgnoreCase);

        if (options.TryGetValue("left", out var left) && options.TryGetValue("right", out var right))
        {
            if (!byId.TryGetValue(left, out var l)) { Console.Error.WriteLine($"Unknown record: {left}"); return 2; }
            if (!byId.TryGetValue(right, out var r)) { Console.Error.WriteLine($"Unknown record: {right}"); return 2; }

            var shared = l.AllKeys.Intersect(r.AllKeys, StringComparer.OrdinalIgnoreCase).ToList();
            var active = shared.Where(k => !suppressedSizes.ContainsKey(k)).ToList();
            var suppressed = shared.Where(suppressedSizes.ContainsKey).ToList();

            Console.WriteLine(BlockingAuditTextFormatter.FormatRecord(l));
            Console.WriteLine(BlockingAuditTextFormatter.FormatRecord(r));
            if (active.Count > 0)
                Console.WriteLine($"WOULD COMPARE (shares {string.Join(", ", active)})");
            else if (suppressed.Count > 0)
                Console.WriteLine(
                    $"SKIPPED (all shared keys suppressed: {string.Join(", ", suppressed.Select(k => $"{k} (size {suppressedSizes[k]}, corpus frequency {suppressedSizes[k] - 1} > {result.Suppression!.MaxBlockSize})"))})");
            else
                Console.WriteLine("SKIPPED (no shared key)");
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
}
