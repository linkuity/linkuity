using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Linkuity.Matching;
using Linkuity.Matching.Clustering;
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
/// <para>
/// STDOUT carries the report and nothing else; every annotation, acknowledgement and verdict goes
/// to STDERR. Under <c>--format csv</c> the report is an artifact meant to be diffed, and a verdict
/// line appended to it corrupts the very output whose reason for existing is that it diffs cleanly.
/// </para>
/// </summary>
public static class CorpusAuditCommands
{
    private const string Usage = """
        Usage: match corpus audit [options]

          Record source (exactly one, inherited from `match blocking` / `match scoring`):
            --input <csv>                 CSV corpus. REQUIRED in gate mode — it is the only
                                          source the gate can pin by SHA-256.
            --metadata <path>             File metadata store (needs --project-id).
            --metadata-store postgres     Postgres store (needs --connection-string, --project-id).

          Required:
            --profile <name|file.json>    In gate mode this must be a FILE, not a built-in name.
            --ground-truth <csv>          Columns: record_id, canonical_key.

          Reporting:
            --format <text|csv>           Default text. STDOUT carries the report alone; verdicts
                                          and annotations go to STDERR so CSV stays diffable.
            --top <n>                     Missed true pairs to list (default 20).
            --max-block-size <n>          Overrides the profile's maxBlockSize.

          Baseline gate (mutually exclusive; both require --corpus-source):
            --write-baseline <dir>        Write baseline.json + baseline-strata.csv.
            --replace-baseline            Permit overwriting an existing baseline.
            --compare-baseline <dir>      Compare this run against a frozen baseline.
            --corpus-source <path>        The snapshot/manifest/extract the corpus was built from.
                                          Pinned by SHA-256 so snapshot drift is enforced, not
                                          merely documented (spec §13).
            --accept-profile-change       Waive the SYSTEM-UNDER-TEST refusals (profile, block
                                          size, thresholds) for this run, and say so in the report.
                                          Evaluation inputs are never waived.

          Exit codes: 0 pass or report-only, 1 gate FAILURE, 2 usage error or gate REFUSAL.
        """;

    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length < 3 || !string.Equals(args[2], "audit", StringComparison.OrdinalIgnoreCase))
        {
            await Console.Error.WriteLineAsync(Usage);
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

        // Checked from the flags alone, before anything is loaded: a gate whose inputs cannot be
        // hashed is not a gate at all (see UnhashableGateInput).
        if (gateMode && UnhashableGateInput(options, profilePath) is { } unhashable)
        {
            await Console.Error.WriteLineAsync(unhashable);
            return 2;
        }

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
        try { truth = AuditCliCommon.ReadGroundTruthStrict(truthPath); }
        catch (ArgumentException ex) { await Console.Error.WriteLineAsync(ex.Message); return 2; }

        var (maxBlockSize, maxErr) = AuditCliCommon.ResolveMaxBlockSize(options, profile);
        if (maxErr is not null) { await Console.Error.WriteLineAsync(maxErr); return 2; }

        CorpusAuditResult result;
        try
        {
            // Passed explicitly rather than left to CorpusAuditService's own default: this is the
            // one caller that reports on real profiles, so it must never lean on a fallback the
            // audit only carries to keep test call sites from having to thread a policy through.
            result = new CorpusAuditService(MatchingDefaults.CreateRegistry(), new CohesionClusterMergePolicy())
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

        // Every hash here is a real SHA-256 of a real file: UnhashableGateInput has already refused
        // the run otherwise, so no placeholder can reach the artifact.
        var inputs = new BaselineInputs(
            Sha256File(options["input"]),
            Sha256File(truthPath),
            Sha256File(profilePath),
            Sha256File(options["corpus-source"]),
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

        // Report to stdout; the confirmation to stderr, so `--format csv > baseline-report.csv`
        // captures the report alone.
        Console.Write(Render(result, options, top));
        await Console.Error.WriteLineAsync(
            $"{Environment.NewLine}Baseline written to " +
            $"{Path.Combine(directory, CorpusAuditBaseline.BaselineFileName)}");
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
        // JsonException is NOT an ArgumentException: without it a truncated or hand-mangled
        // baseline.json escapes as an unhandled crash instead of exit 2, and Program.cs has no
        // top-level handler. WriteAtomic's own doc comment warns that a crash between its two file
        // moves leaves a torn artifact — this is the read side of exactly that state.
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                      or JsonException or IOException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 2;
        }

        var comparison = CorpusAuditBaseline.Compare(
            baseline, currentInFrozenCohorts, acceptProfileChange, reclassified);

        // Report BEFORE the acknowledgement, warning and verdict: a PASSED/FAILED/REFUSED line with
        // no visible numbers beside it is not actionable. Ordering on a terminal is guaranteed by
        // this write order; the split across stdout/stderr is what keeps `--format csv` diffable.
        Console.Write(Render(result, options, top));

        // Spec §10: the acknowledgement must be typed deliberately AND echoed in the report.
        // Without this, a `GATE PASSED.` transcript from a run that WAIVED the profile refusal is
        // byte-indistinguishable from one where the profile never moved — the gate becoming
        // skippable in the artifact a human actually reads.
        if (acceptProfileChange)
            await Console.Error.WriteLineAsync(
                $"{Environment.NewLine}ACKNOWLEDGED: --accept-profile-change was given, so the " +
                "system-under-test refusals (profileSha256, maxBlockSize, autoMatchThreshold, " +
                "reviewThreshold, reviewFloorGate) were WAIVED for this run. The verdict below was " +
                "reached against a configuration that may differ from the baseline's. Evaluation " +
                "inputs — records, ground truth, corpus source, frozen strata — were still enforced.");

        if (comparison.ReclassifiedPairs > 0)
            await Console.Error.WriteLineAsync(
                $"{Environment.NewLine}WARNING: " +
                $"{comparison.ReclassifiedPairs.ToString("N0", CultureInfo.InvariantCulture)} " +
                "true pair(s) changed stratum since the baseline. Gated cohorts are bucketed by the " +
                "BASELINE assignment, not by the classification in the report above.");

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

        await Console.Error.WriteLineAsync($"{Environment.NewLine}GATE PASSED.");
        return 0;
    }

    // ---- helpers ----

    /// <summary>
    /// Gate mode refuses any evaluation input it cannot reduce to a SHA-256, naming which one and
    /// why. A placeholder in the artifact is worse than no artifact: <c>""</c> equals <c>""</c>, so
    /// a baseline written with an empty <c>recordsSha256</c> records a refusal that can NEVER fire
    /// — a later run over an entirely different corpus would compare clean. Spec §13 requires
    /// snapshot drift to be "pinned by SHA-256 in the manifest and ENFORCED BY THE GATE, not merely
    /// documented". Report-only runs are unaffected; nothing is being pinned there.
    /// <para>Returns null when every input is hashable.</para>
    /// </summary>
    private static string? UnhashableGateInput(
        IReadOnlyDictionary<string, string> options, string profilePath)
    {
        if (!options.TryGetValue("input", out var inPath) || !File.Exists(inPath))
            return "Gate mode requires a hashable record source: pass --input <csv>. A store-backed " +
                   "source (--metadata / --metadata-store postgres) has no single file to hash, so " +
                   "the baseline would record an empty recordsSha256 — and an empty hash matches " +
                   "every other empty hash, so the records refusal could never fire. Writing or " +
                   "comparing a baseline over a corpus the gate cannot pin is not a gate.";

        if (!File.Exists(profilePath))
            return $"Gate mode requires a hashable profile: '{profilePath}' resolves to a built-in " +
                   "profile, not a file. The baseline would record that NAME where a hash belongs, " +
                   "so any later edit to the built-in profile's definition would be invisible to the " +
                   "profileSha256 refusal (spec §5.1). Write the profile out as a *.profile.json " +
                   "file and pass its path.";

        if (!options.TryGetValue("corpus-source", out var src) || !File.Exists(src))
            return "Gate mode requires --corpus-source <path>: the snapshot, manifest or extract the " +
                   "corpus was built from. Spec §13 pins snapshot drift by SHA-256 and requires the " +
                   "GATE to enforce it; without the flag the baseline records an empty " +
                   "corpusSourceSha256, which matches every other empty value and can never refuse.";

        return null;
    }

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

}
