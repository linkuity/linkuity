using System.Globalization;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Profiles.Configuration;
using Linkuity.Pipeline;

namespace Linkuity.Cli;

/// <summary>
/// `match corpus ablate`: runs the same labelled corpus through the same profile at several
/// Matchable-field widths and reports, per width, the number of matchable fields, reachability,
/// and the best-recall point at 100% direct-edge precision (or an explicit statement that no cut
/// reaches 100% precision). Blocking is never touched by a width — only Matchable roles vary —
/// so the candidate set stays fixed and only the scoring input changes. Report-only: there is no
/// baseline and no gate, because this decides whether a THRESHOLD problem exists, not whether one
/// regressed. Record source flags are inherited from <see cref="AuditCliCommon"/>.
/// <para>Exit codes: 0 report produced, 2 usage or validation error.</para>
/// </summary>
public static class FieldShapeAblationCommands
{
    private const string Usage = """
        Usage: match corpus ablate [options]

          Record source (exactly one, inherited from `match corpus audit`):
            --input <csv>                 CSV corpus.
            --metadata <path>             File metadata store (needs --project-id).
            --metadata-store postgres     Postgres store (needs --connection-string, --project-id).

          Required:
            --profile <name|file.json>    The FULL-WIDTH profile; each width narrows its Matchable
                                          set. Blocking roles are never touched by any width.
            --ground-truth <csv>          Columns: record_id, canonical_key.
            --widths <spec>               ';'-separated widths, each 'name=field1,field2,...'.
                                          Every named field must already be Matchable on
                                          --profile: ablation only narrows, it never widens.
                                          Example:
                                            "name=first_name,last_name;full=first_name,last_name,
                                            date_of_birth,soc_sec_id"

          Reporting:
            --max-block-size <n>          Overrides the profile's maxBlockSize. Held constant
                                          across every width so only the scoring input varies.

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
        if (!options.TryGetValue("widths", out var widthsSpec) || string.IsNullOrWhiteSpace(widthsSpec))
        {
            await Console.Error.WriteLineAsync("A --widths spec is required. " + Usage);
            return 2;
        }

        IReadOnlyList<FieldWidth> widths;
        try { widths = ParseWidths(widthsSpec); }
        catch (ArgumentException ex) { await Console.Error.WriteLineAsync(ex.Message); return 2; }

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

        FieldShapeAblationResult result;
        try
        {
            result = new FieldShapeAblationService(MatchingDefaults.CreateRegistry())
                .Audit(records, profile, truth, widths, maxBlockSize);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 2;
        }

        Console.Write(FieldShapeAblationTextFormatter.Format(result, profile.ScoringStrategy));
        return 0;
    }

    /// <summary>
    /// Parses "name=field1,field2;name2=field3,field4" into widths, in the order given. A width
    /// name may itself contain '=' or ';'... it may not: those characters are the delimiters, so
    /// a name is split on the FIRST '=' only, and widths are split on ';' with no escaping.
    /// </summary>
    internal static IReadOnlyList<FieldWidth> ParseWidths(string spec)
    {
        var widths = new List<FieldWidth>();
        foreach (var chunk in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = chunk.IndexOf('=');
            if (eq < 0)
                throw new ArgumentException(
                    $"Invalid --widths entry '{chunk}': expected 'name=field1,field2,...'.");
            var name = chunk[..eq].Trim();
            var fields = chunk[(eq + 1)..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (name.Length == 0)
                throw new ArgumentException($"Invalid --widths entry '{chunk}': empty width name.");
            if (fields.Count == 0)
                throw new ArgumentException($"Invalid --widths entry '{chunk}': no fields named.");
            widths.Add(new FieldWidth(name, fields));
        }
        if (widths.Count == 0)
            throw new ArgumentException("--widths named no widths.");
        return widths;
    }
}
