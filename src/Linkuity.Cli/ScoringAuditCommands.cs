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

    // Explain lands in Task 4.
    private static int Explain(
        ScoringAuditService service, IReadOnlyList<Linkuity.Core.Models.EntityRecord> records,
        MatchingProfile profile, IReadOnlyDictionary<string, string> options)
    {
        Console.Error.WriteLine("match scoring explain is implemented in Task 4.");
        return 2;
    }
}
