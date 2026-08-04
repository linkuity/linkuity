using System.Globalization;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Profiles.Configuration;
using Linkuity.Pipeline;

namespace Linkuity.Cli;

/// <summary>
/// `match corpus calibrate`: estimates m (same-entity agreement) and u (chance agreement) per
/// matchable field from a labelled corpus, for the evidence scorer's <see cref="FieldEvidence"/>
/// parameters. Report-only — this produces numbers and the evidence for judging them; it never
/// edits a profile, never switches a profile to the evidence scorer, and never picks a threshold.
/// Record source flags are inherited from <see cref="AuditCliCommon"/>, same as the sibling
/// `match corpus` verbs.
/// <para>Exit codes: 0 report produced, 2 usage or validation error.</para>
/// </summary>
public static class FieldEvidenceCalibrationCommands
{
    private const string Usage = """
        Usage: match corpus calibrate [options]

          Record source (exactly one, inherited from `match corpus audit`):
            --input <csv>                 CSV corpus.
            --metadata <path>             File metadata store (needs --project-id).
            --metadata-store postgres     Postgres store (needs --connection-string, --project-id).

          Required:
            --profile <name|file.json>    Must use normalizationStrategy 'identity' and
                                          similarityStrategy 'field-weighted'.
            --ground-truth <csv>          Columns: record_id, canonical_key.

          Reporting:
            --max-block-size <n>          Overrides the profile's maxBlockSize.
            --fit-fraction <0..1>         Target share of records assigned to the fit half by a
                                          deterministic hash of record id. Default 0.5. The eval
                                          half is never loaded into the candidate walk below.

          Exit codes: 0 report produced, 2 usage or validation error.
        """;

    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var options = AuditCliCommon.ParseFlags(args.Skip(3));

        if (!options.TryGetValue("profile", out var profilePath) || string.IsNullOrWhiteSpace(profilePath))
        {
            await Console.Error.WriteLineAsync(Usage);
            return 2;
        }
        if (!options.TryGetValue("ground-truth", out var truthPath) || !File.Exists(truthPath))
        {
            await Console.Error.WriteLineAsync($"Ground-truth CSV not found: {truthPath}");
            return 2;
        }

        var fitFraction = 0.5;
        if (options.TryGetValue("fit-fraction", out var fitRaw) &&
            (!double.TryParse(fitRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out fitFraction) ||
             fitFraction <= 0 || fitFraction >= 1))
        {
            await Console.Error.WriteLineAsync($"Invalid --fit-fraction value: {fitRaw}");
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

        FieldEvidenceCalibrationResult result;
        try
        {
            result = new FieldEvidenceCalibrationService(MatchingDefaults.CreateRegistry())
                .Calibrate(records, profile, truth, maxBlockSize, fitFraction, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 2;
        }

        Console.Write(FieldEvidenceCalibrationTextFormatter.Format(result));
        return 0;
    }
}
