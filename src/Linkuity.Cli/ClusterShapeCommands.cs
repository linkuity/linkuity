using System.Globalization;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Profiles.Configuration;
using Linkuity.Pipeline;

namespace Linkuity.Cli;

/// <summary>
/// `match corpus shape`: measures whether correct clusters and over-merged ones have different
/// internal shapes, given only the comparisons the engine actually performs. Report-only — there
/// is no baseline and no gate, because this decides whether a rule is possible, not whether one
/// regressed. Record source flags are inherited from <see cref="AuditCliCommon"/>.
/// <para>Exit codes: 0 report produced, 2 usage or validation error.</para>
/// </summary>
public static class ClusterShapeCommands
{
    private const string Usage = """
        Usage: match corpus shape [options]

          Record source (exactly one, inherited from `match corpus audit`):
            --input <csv>                 CSV corpus.
            --metadata <path>             File metadata store (needs --project-id).
            --metadata-store postgres     Postgres store (needs --connection-string, --project-id).

          Required:
            --profile <name|file.json>
            --ground-truth <csv>          Columns: record_id, canonical_key. Clusters with no
                                          labelled member are reported as unlabeled, never as
                                          correct.

          Reporting:
            --top <n>                     Largest clusters to list (default 20).
            --max-block-size <n>          Overrides the profile's maxBlockSize.

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

        // Validated before the run: this is measured in minutes, and discovering a bad --top
        // afterwards is pure waste.
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

        ClusterShapeResult result;
        try
        {
            result = new ClusterShapeAuditService(MatchingDefaults.CreateRegistry())
                .Audit(records, profile, truth, maxBlockSize, top, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 2;
        }

        Console.Write(ClusterShapeTextFormatter.Format(result, top));
        return 0;
    }
}
