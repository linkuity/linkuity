using System.Globalization;
using System.Text;
using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Profiles.Configuration;
using Linkuity.Pipeline;

namespace Linkuity.Cli;

/// <summary>
/// `match corpus fields` — which columns are worth matching on, measured rather than guessed, and
/// what this data can never resolve.
///
/// The report is written for someone deciding what to put in a profile, not for someone reading
/// log-odds. Its central point is that the two agreement rates only mean something TOGETHER: a
/// field where the same entity agrees 88% of the time looks excellent until the column beside it
/// shows unrelated records agreeing 22% of the time as well.
/// </summary>
public static class FieldUsefulnessCommands
{
    private const string Usage = """
        Usage: match corpus fields [options]

            --input <path>            Records CSV. Required.
            --ground-truth <path>     record_id,canonical_key CSV. Required.
            --profile <path>          Matching profile JSON. Required.
            --max-block-size <n>      Overrides the profile's maxBlockSize.
        """;

    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var options = AuditCliCommon.ParseFlags(args.Skip(3));

        if (!options.TryGetValue("profile", out var profilePath) || string.IsNullOrWhiteSpace(profilePath))
        {
            await Console.Error.WriteLineAsync("--profile is required.");
            await Console.Error.WriteLineAsync(Usage);
            return 2;
        }
        // "not supplied" and "supplied but missing" are different mistakes and get different
        // messages: reporting an absent flag as a file not found at the empty path sends the reader
        // looking for a path problem they do not have.
        if (!options.TryGetValue("ground-truth", out var truthPath) || string.IsNullOrWhiteSpace(truthPath))
        {
            await Console.Error.WriteLineAsync(
                "--ground-truth is required: this report works by comparing how often confirmed-same " +
                "records agree against how often confirmed-different records agree, so it cannot run " +
                "without knowing which is which.");
            await Console.Error.WriteLineAsync(Usage);
            return 2;
        }
        if (!File.Exists(truthPath))
        {
            await Console.Error.WriteLineAsync($"Ground-truth CSV not found: {truthPath}");
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

        Dictionary<string, string> truth;
        try { truth = AuditCliCommon.ReadGroundTruthStrict(truthPath); }
        catch (ArgumentException ex) { await Console.Error.WriteLineAsync(ex.Message); return 2; }

        var (maxBlockSize, maxErr) = AuditCliCommon.ResolveMaxBlockSize(options, profile);
        if (maxErr is not null) { await Console.Error.WriteLineAsync(maxErr); return 2; }

        FieldUsefulnessResult result;
        try
        {
            result = new FieldUsefulnessService(MatchingDefaults.CreateRegistry())
                .Analyze(records, profile, truth, maxBlockSize, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 2;
        }

        Console.Write(Render(result));
        return 0;
    }

    internal static string Render(FieldUsefulnessResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();

        sb.AppendLine("=== field usefulness ===");
        sb.AppendLine(inv, $"{r.Records:N0} records, {r.LabeledRecords:N0} labeled, " +
                           $"{r.SameEntityPairsObserved:N0} confirmed same-entity pairs observed");
        sb.AppendLine();
        sb.AppendLine("A field is useful when the two middle columns are FAR APART. A field both");
        sb.AppendLine("the same entity and unrelated records agree on tells you almost nothing.");
        sb.AppendLine();
        sb.AppendLine("  field                     filled   same entity   different   evidence   verdict");
        sb.AppendLine("                                         agrees     agree      per match");
        sb.AppendLine("  " + new string('-', 78));

        foreach (var f in r.Fields)
        {
            sb.AppendLine(inv,
                $"  {Trim(f.FieldName, 24),-24} {f.FillRate,7:P0}   {Pct(f.SameEntityAgreement),9}   " +
                $"{Pct(f.ChanceAgreement),9}   {Bits(f.Bits),9}   {f.Verdict}");
        }

        var thin = r.Fields.Where(f => f.Bits is null).Select(f => f.FieldName).ToList();
        if (thin.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(inv, $"  Not measured (too few observations): {string.Join(", ", thin)}.");
            sb.AppendLine("  Reported as unmeasured rather than as zero — \"we could not tell\" and");
            sb.AppendLine("  \"this is worthless\" are different answers.");
        }

        var ind = r.Indistinguishable;
        sb.AppendLine();
        sb.AppendLine("--- what this data cannot resolve ---");
        if (ind.Groups == 0)
        {
            sb.AppendLine("  Every labeled record is distinguishable from every other entity's records.");
        }
        else
        {
            sb.AppendLine(inv,
                $"  {ind.RecordsInvolved:N0} record(s) in {ind.Groups:N0} group(s) are identical on every matchable field");
            sb.AppendLine(inv,
                $"  yet belong to different entities. Largest group: {ind.LargestGroupRecords:N0} record(s) " +
                $"spanning {ind.LargestGroupEntities:N0} entities.");
            if (ind.LargestGroupUnfilledFields.Count > 0)
            {
                var tail = ind.LargestGroupUnfilledFields.Count == 1
                    ? "collecting it is what would separate them."
                    : "collecting one of these is what would separate them.";
                sb.AppendLine(inv,
                    $"  Unfilled throughout that group: {string.Join(", ", ind.LargestGroupUnfilledFields)} - {tail}");
            }
            sb.AppendLine();
            sb.AppendLine("  No threshold can fix these, and they are not permission to merge them:");
            sb.AppendLine("  a pair nothing distinguishes belongs in review, or apart.");
        }

        return sb.ToString();
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

    private static string Pct(double? value) =>
        value is { } v ? v.ToString("P2", CultureInfo.InvariantCulture) : "n/a";

    private static string Bits(double? value) =>
        value is { } v ? v.ToString("0.0", CultureInfo.InvariantCulture) + " bits" : "n/a";
}
