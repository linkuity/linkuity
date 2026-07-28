using System.Globalization;
using System.Security.Cryptography;
using CsvHelper;
using CsvHelper.Configuration;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Profiles.Configuration;
using Linkuity.Pipeline;

namespace Linkuity.Cli;

/// <summary>
/// `match corpus audit`: scale-capable recall and precision audit against a labelled corpus,
/// with an optional machine-enforced baseline gate. Record source flags are inherited from
/// <see cref="AuditCliCommon"/> (--input / --metadata / --metadata-store), so this command
/// follows the same conventions as `match blocking` and `match scoring`.
/// <para>
/// Exit codes are a contract, and 1 and 2 must never be collapsed:
/// <list type="bullet">
/// <item><description><c>0</c> — a report-only run, a successful baseline write, or a gate that passed.</description></item>
/// <item><description><c>1</c> — gate FAILURE: the two runs are comparable and something got worse.</description></item>
/// <item><description><c>2</c> — usage/validation error, or gate REFUSAL: the runs are not comparable at all.
/// Reporting a refusal as a failure sends someone debugging a regression that does not exist.</description></item>
/// </list>
/// </para>
/// </summary>
public static class CorpusAuditCommands
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length < 3 || !string.Equals(args[2], "audit", StringComparison.OrdinalIgnoreCase))
        {
            await Console.Error.WriteLineAsync("Usage: match corpus audit [options].");
            return 2;
        }

        var options = AuditCliCommon.ParseFlags(args.Skip(3));

        if (!options.TryGetValue("profile", out var profilePath) || string.IsNullOrWhiteSpace(profilePath))
        {
            await Console.Error.WriteLineAsync("A --profile is required.");
            return 2;
        }
        if (!options.TryGetValue("ground-truth", out var truthPath) || !File.Exists(truthPath))
        {
            await Console.Error.WriteLineAsync($"Ground-truth CSV not found: {truthPath}");
            return 2;
        }

        var write = DirectoryFlag(options, "write-baseline");
        var compare = DirectoryFlag(options, "compare-baseline");
        if (write is not null && compare is not null)
        {
            await Console.Error.WriteLineAsync("Use --write-baseline or --compare-baseline, not both.");
            return 2;
        }
        var replace = options.ContainsKey("replace-baseline");

        // Spec §10 (amended 2026-07-28): the EVALUATION INPUTS (records, ground truth, corpus
        // source, frozen strata) always refuse on mismatch; only the SYSTEM UNDER TEST (profile,
        // block size, thresholds) can be acknowledged with this flag. Default is unchanged.
        var acceptProfileChange = options.ContainsKey("accept-profile-change");
        var gateMode = write is not null || compare is not null;

        // Validated BEFORE the audit runs: a corpus-scale audit is measured in minutes, and
        // discovering a bad --top after it is pure waste.
        var top = 20;
        if (options.TryGetValue("top", out var topRaw) &&
            (!int.TryParse(topRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out top) || top < 1))
        {
            await Console.Error.WriteLineAsync($"Invalid --top value: {topRaw}");
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

        Dictionary<string, string> truth;
        try { truth = ReadGroundTruth(truthPath); }
        catch (ArgumentException ex) { await Console.Error.WriteLineAsync(ex.Message); return 2; }

        var (maxBlockSize, maxErr) = AuditCliCommon.ResolveMaxBlockSize(options, profile);
        if (maxErr is not null) { await Console.Error.WriteLineAsync(maxErr); return 2; }

        CorpusAuditResult result;
        try
        {
            result = new CorpusAuditService(MatchingDefaults.CreateRegistry())
                .Audit(records, profile, truth, maxBlockSize, gateMode, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 2;
        }

        if (!gateMode)
        {
            Console.Write(Render(result, options, top));
            return 0;
        }

        var frozen = result.AllTruePairs
            .Select(p => new FrozenStratumAssignment(p.LeftSourceRecordId, p.RightSourceRecordId, p.Stratum))
            .ToList();

        var inputs = new BaselineInputs(
            options.TryGetValue("input", out var inPath) && File.Exists(inPath) ? Sha256File(inPath) : "",
            Sha256File(truthPath),
            File.Exists(profilePath) ? Sha256File(profilePath) : profilePath,
            options.TryGetValue("corpus-source", out var src) && File.Exists(src) ? Sha256File(src) : "",
            CorpusAuditBaseline.Sha256Of(CorpusAuditBaseline.WriteStrataCsv(frozen)),
            result.Inputs.EffectiveMaxBlockSize,
            profile.AutoMatchThreshold, profile.ReviewThreshold, profile.ReviewFloorGate);

        var current = CorpusAuditBaseline.Create(
            result, inputs, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));

        return write is not null
            ? await WriteBaselineAsync(write, current, frozen, replace, result, options, top)
            : await CompareBaselineAsync(compare!, current, result, acceptProfileChange, options, top, ct);
    }

    // ---- --write-baseline ----

    private static async Task<int> WriteBaselineAsync(
        string directory, CorpusAuditBaseline current, IReadOnlyList<FrozenStratumAssignment> frozen,
        bool replace, CorpusAuditResult result, IReadOnlyDictionary<string, string> options, int top)
    {
        try { CorpusAuditBaseline.WriteAtomic(directory, current, frozen, replace); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // WriteAtomic refuses to overwrite an existing baseline unless --replace-baseline was
            // given; its message explains why, so it is surfaced intact rather than paraphrased.
            await Console.Error.WriteLineAsync(ex.Message);
            return 2;
        }

        Console.Write(Render(result, options, top));
        Console.WriteLine();
        Console.WriteLine($"Baseline written to {Path.Combine(directory, CorpusAuditBaseline.BaselineFileName)}");
        return 0;
    }

    // ---- --compare-baseline ----

    private static async Task<int> CompareBaselineAsync(
        string directory, CorpusAuditBaseline current, CorpusAuditResult result,
        bool acceptProfileChange, IReadOnlyDictionary<string, string> options, int top, CancellationToken ct)
    {
        var jsonPath = Path.Combine(directory, CorpusAuditBaseline.BaselineFileName);
        var strataPath = Path.Combine(directory, CorpusAuditBaseline.StrataFileName);
        if (!File.Exists(jsonPath) || !File.Exists(strataPath))
        {
            await Console.Error.WriteLineAsync(
                $"Baseline incomplete in '{directory}': need {CorpusAuditBaseline.BaselineFileName} " +
                $"and {CorpusAuditBaseline.StrataFileName}.");
            return 2;
        }

        CorpusAuditBaseline baseline;
        IReadOnlyList<FrozenStratumAssignment> baselineStrata;
        string strataText;
        long reclassified;
        CorpusAuditBaseline currentInFrozenCohorts;
        try
        {
            baseline = CorpusAuditBaseline.FromJson(await File.ReadAllTextAsync(jsonPath, ct));

            // The sidecar is hashed AS TEXT READ FROM DISK, never as raw bytes: File.ReadAllTextAsync
            // strips a UTF-8 BOM and Sha256Of re-encodes the string, so a byte-level difference that
            // leaves the content identical cannot spuriously fail the integrity check. The two-arg
            // overload is the only public route precisely so this check cannot be skipped.
            strataText = await File.ReadAllTextAsync(strataPath, ct);
            baselineStrata = CorpusAuditBaseline.ReadStrataCsv(strataText, baseline.Inputs.StrataSha256);

            // Never a literal 0: §8.1 reports reclassification rather than gating it, and a
            // forgotten count would read as "nothing moved" indistinguishably from the truth.
            reclassified = CorpusAuditBaseline.CountReclassified(result.AllTruePairs, baselineStrata);

            currentInFrozenCohorts = current with
            {
                // strataSha256 is a REFUSAL input for the sidecar's identity, not for the current
                // run's classification — which §8.1 expects to differ. Carrying the sidecar's own
                // hash forward is what keeps every informative run comparable.
                Inputs = current.Inputs with { StrataSha256 = CorpusAuditBaseline.Sha256Of(strataText) },

                // AggregateByFrozenStratum, NOT Create: cohorts must be bucketed by the BASELINE's
                // frozen assignment. Bucketing by the current run's classification would let a
                // canonicalizer change move a hard pair into an excluded stratum and report
                // improvement during a regression (spec §8.1).
                Strata = CorpusAuditBaseline.AggregateByFrozenStratum(result.AllTruePairs, baselineStrata)
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                      or IOException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 2;
        }

        var comparison = CorpusAuditBaseline.Compare(
            baseline, currentInFrozenCohorts, acceptProfileChange, reclassified);

        // Report BEFORE the verdict: a PASSED/FAILED/REFUSED line with no visible numbers beside
        // it is not actionable.
        Console.Write(Render(result, options, top));
        if (comparison.ReclassifiedPairs > 0)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"WARNING: {comparison.ReclassifiedPairs.ToString("N0", CultureInfo.InvariantCulture)} " +
                "true pair(s) changed stratum since the baseline. Gated cohorts are bucketed by the " +
                "BASELINE assignment, not by the classification above.");
        }

        if (comparison.Refused)
        {
            await Console.Error.WriteLineAsync($"{Environment.NewLine}GATE REFUSED. {comparison.RefusalReason}");
            return 2;
        }
        if (comparison.Failures.Count > 0)
        {
            await Console.Error.WriteLineAsync($"{Environment.NewLine}GATE FAILED:");
            foreach (var failure in comparison.Failures)
                await Console.Error.WriteLineAsync($"  - {failure}");
            await Console.Error.WriteLineAsync(
                $"{Environment.NewLine}Gate failure means stop and escalate, not adjust-until-green.");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("GATE PASSED.");
        return 0;
    }

    // ---- helpers ----

    /// <summary>A directory-valued flag. ParseFlags stores a bare `--flag` as "true", which is
    /// never a directory the caller meant, so it is rejected rather than silently used.</summary>
    private static string? DirectoryFlag(IReadOnlyDictionary<string, string> options, string name)
        => options.TryGetValue(name, out var value)
           && !string.IsNullOrWhiteSpace(value)
           && !string.Equals(value, "true", StringComparison.Ordinal)
            ? value
            : null;

    private static string Render(CorpusAuditResult result, IReadOnlyDictionary<string, string> options, int top)
        => options.TryGetValue("format", out var fmt) && string.Equals(fmt, "csv", StringComparison.OrdinalIgnoreCase)
            ? CorpusAuditCsvFormatter.Format(result)
            : CorpusAuditTextFormatter.Format(result, top);

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static Dictionary<string, string> ReadGroundTruth(string path)
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
}
