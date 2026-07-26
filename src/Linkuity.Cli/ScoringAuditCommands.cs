using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Profiles.Configuration;
using Linkuity.Pipeline;

namespace Linkuity.Cli;

/// <summary>
/// `match scoring audit` and `match scoring explain`: score candidate pairs under a
/// profile (batch blocking-linear fidelity) and report band outcomes, direct-edge
/// P/R/F1, threshold sweep, miss decomposition, and per-field diagnostics. See
/// docs/superpowers/specs/2026-07-26-scoring-audit-instrument-design.md.
/// </summary>
public static class ScoringAuditCommands
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length < 3)
        {
            await Console.Error.WriteLineAsync("Usage: match scoring <audit|explain> [options].");
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

        IReadOnlyList<Linkuity.Core.Models.EntityRecord> records;
        try { records = await AuditCliCommon.LoadRecordsAsync(options, ct); }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or InvalidOperationException)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 2;
        }

        var service = new ScoringAuditService(MatchingDefaults.CreateRegistry());

        try
        {
            return verb.ToLowerInvariant() switch
            {
                "audit" => await AuditAsync(service, records, profile, options),
                "explain" => Explain(service, records, profile, options),
                _ => await UnknownVerbAsync(verb)
            };
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            // ArgumentException: v1 strategy constraint, duplicate ids, bad thresholds.
            // KeyNotFoundException: evaluator lookup for code-constructed profiles.
            await Console.Error.WriteLineAsync(ex.Message);
            return 2;
        }
    }

    private static async Task<int> UnknownVerbAsync(string verb)
    {
        await Console.Error.WriteLineAsync($"Unknown scoring verb '{verb}'. Expected 'audit' or 'explain'.");
        return 2;
    }

    private static async Task<int> AuditAsync(
        ScoringAuditService service, IReadOnlyList<Linkuity.Core.Models.EntityRecord> records,
        MatchingProfile profile, IReadOnlyDictionary<string, string> options)
    {
        IReadOnlyDictionary<string, string>? groundTruth = null;
        if (options.TryGetValue("ground-truth", out var gtPath) && !string.IsNullOrWhiteSpace(gtPath))
        {
            if (!File.Exists(gtPath))
            {
                await Console.Error.WriteLineAsync($"Ground-truth CSV not found: {gtPath}");
                return 2;
            }
            groundTruth = ReadGroundTruthStrict(gtPath);
        }

        var (maxBlockSize, maxErr) = AuditCliCommon.ResolveMaxBlockSize(options, profile);
        if (maxErr is not null) { await Console.Error.WriteLineAsync(maxErr); return 2; }

        var (auto, review, thrErr) = ParseThresholdOverrides(options);
        if (thrErr is not null) { await Console.Error.WriteLineAsync(thrErr); return 2; }

        var top = 20;
        if (options.TryGetValue("top", out var topRaw) &&
            (!int.TryParse(topRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out top) || top < 1))
        {
            await Console.Error.WriteLineAsync($"Invalid --top value: {topRaw}");
            return 2;
        }

        var result = service.Audit(records, profile, groundTruth, maxBlockSize, auto, review);

        var format = options.TryGetValue("format", out var fmt) ? fmt : "text";
        Console.Write(string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase)
            ? ScoringAuditCsvFormatter.Format(result, profile)
            : ScoringAuditTextFormatter.Format(result, top));
        return 0;
    }

    private static (double? Auto, double? Review, string? Error) ParseThresholdOverrides(
        IReadOnlyDictionary<string, string> options)
    {
        double? Parse(string key, out string? error)
        {
            error = null;
            if (!options.TryGetValue(key, out var raw)) return null;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v))
            {
                error = $"Invalid --{key} value: {raw}";
                return null;
            }
            return v;
        }
        var auto = Parse("auto-threshold", out var e1);
        if (e1 is not null) return (null, null, e1);
        var review = Parse("review-threshold", out var e2);
        if (e2 is not null) return (null, null, e2);
        return (auto, review, null);
        // Range/ordering validation (0 <= review < auto <= 1) lives in the service.
    }

    /// <summary>Like the blocking reader, but duplicate record_id rows fail fast (spec).</summary>
    private static IReadOnlyDictionary<string, string> ReadGroundTruthStrict(string path)
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

    private static int Explain(
        ScoringAuditService service, IReadOnlyList<Linkuity.Core.Models.EntityRecord> records,
        MatchingProfile profile, IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("left", out var left) || !options.TryGetValue("right", out var right))
        {
            Console.Error.WriteLine("Provide --left <id> --right <id>.");
            return 2;
        }
        var byId = records.ToDictionary(r => r.SourceRecordId, StringComparer.Ordinal);
        if (!byId.ContainsKey(left)) { Console.Error.WriteLine($"Unknown record: {left}"); return 2; }
        if (!byId.ContainsKey(right)) { Console.Error.WriteLine($"Unknown record: {right}"); return 2; }

        var (maxBlockSize, maxErr) = AuditCliCommon.ResolveMaxBlockSize(options, profile);
        if (maxErr is not null) { Console.Error.WriteLine(maxErr); return 2; }

        var result = service.Audit(records, profile, groundTruth: null, maxBlockSize);
        var (lo, hi) = ScoringAuditService.Canonical(left, right);
        var pair = result.Pairs.FirstOrDefault(p =>
            p.LeftSourceRecordId == lo && p.RightSourceRecordId == hi);

        // Field values side by side.
        foreach (var field in profile.Fields.Where(f => f.Roles.HasFlag(FieldRole.Matchable)))
        {
            byId[left].Fields.TryGetValue(field.Name, out var lv);
            byId[right].Fields.TryGetValue(field.Name, out var rv);
            Console.WriteLine($"{field.Name} ({field.SimilarityEvaluator ?? "exact"}, w {field.Weight.ToString(CultureInfo.InvariantCulture)}): " +
                $"'{lv ?? ""}' vs '{rv ?? ""}'");
        }

        if (pair is null)
        {
            // Not a candidate: not reachable. Score it offline for the diagnosis.
            var offline = service.Audit(records, profile,
                groundTruth: new Dictionary<string, string>(StringComparer.Ordinal)
                    { [left] = "x", [right] = "x" },
                maxBlockSize)
                .Pairs.First(p => p.LeftSourceRecordId == lo && p.RightSourceRecordId == hi);
            Console.WriteLine("not reachable under this profile's blocking (no shared active key)");
            PrintScore(offline.OfflineScore ?? offline.Score, offline.WouldBeBand ?? offline.EngineBand, offline);
            return 0;
        }

        var sharedKeys = SharedActiveKeys(records, profile, left, right, maxBlockSize);
        Console.WriteLine($"shared keys: {(sharedKeys.Count > 0 ? string.Join(", ", sharedKeys) : "(none)")}");
        PrintScore(pair.Score, pair.EngineBand, pair);
        return 0;

        static void PrintScore(double? score, ScoreBand band, ScoredPair p)
        {
            foreach (var c in p.Breakdown)
                Console.WriteLine(
                    $"  {c.Signal}: sim {c.Value.ToString("F4", CultureInfo.InvariantCulture)} x " +
                    $"w {c.Weight.ToString(CultureInfo.InvariantCulture)} -> " +
                    $"{c.Contribution.ToString("F4", CultureInfo.InvariantCulture)}");
            var weighted = p.Breakdown.Sum(c => c.Contribution);
            if (score is { } s && s > weighted + 1e-9)
                Console.WriteLine(s >= 0.98 ? "identifier floor (0.98) fired" : "review floor (0.80) fired");
            Console.WriteLine(
                $"score {(score is { } v ? v.ToString("F4", CultureInfo.InvariantCulture) : "n/a")} -> {ScoringAuditCsvFormatter.BandName(band)}");
        }
    }

    private static IReadOnlyList<string> SharedActiveKeys(
        IReadOnlyList<Linkuity.Core.Models.EntityRecord> records, MatchingProfile profile,
        string left, string right, int? maxBlockSize)
    {
        var blocking = new BlockingAuditService(MatchingDefaults.CreateRegistry())
            .Audit(records, profile, groundTruth: null, maxCandidates: null, maxBlockSize: null);
        var byId = blocking.PerRecord.ToDictionary(r => r.SourceRecordId, StringComparer.Ordinal);
        var blockSizes = blocking.Blocks.ToDictionary(b => b.Key, b => b.Size, StringComparer.OrdinalIgnoreCase);
        return byId[left].AllKeys
            .Intersect(byId[right].AllKeys, StringComparer.OrdinalIgnoreCase)
            .Where(k => maxBlockSize is not { } max || blockSizes[k] - 1 <= max) // engine parity
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
