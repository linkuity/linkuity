using System.Globalization;
using System.Text;
using Linkuity.Matching.Profiles;
using Linkuity.Pipeline;

namespace Linkuity.Cli;

/// <summary>
/// One row per pair, sorted by canonical pair identity (left_id, right_id ordinal) so
/// two runs across a config change diff row-aligned. Score is a column, never the sort
/// key. sim_&lt;field&gt; columns cover matchable profile fields in ordinal name order.
/// </summary>
public static class ScoringAuditCsvFormatter
{
    public static string Format(ScoringAuditResult result, MatchingProfile profile)
    {
        var fields = profile.Fields
            .Where(f => f.Roles.HasFlag(FieldRole.Matchable))
            .Select(f => f.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        // rank: 1-based by engine score desc over reachable comparable pairs.
        var ranks = new Dictionary<(string, string), int>();
        var rank = 0;
        foreach (var p in result.Pairs
            .Where(p => p.Score is not null)
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.LeftSourceRecordId, StringComparer.Ordinal)
            .ThenBy(p => p.RightSourceRecordId, StringComparer.Ordinal))
            ranks[(p.LeftSourceRecordId, p.RightSourceRecordId)] = ++rank;

        var sb = new StringBuilder();
        sb.Append("left_id,right_id,score,rank,engine_band,would_be_band,is_true,reachable,comparable");
        foreach (var f in fields) sb.Append(CultureInfo.InvariantCulture, $",sim_{f}");
        sb.AppendLine();

        foreach (var p in result.Pairs) // already pair-identity ordered by the service
        {
            var sims = p.Breakdown.ToDictionary(c => c.Signal, c => c.Value, StringComparer.Ordinal);
            sb.Append(CultureInfo.InvariantCulture,
                $"{p.LeftSourceRecordId},{p.RightSourceRecordId}," +
                $"{(p.Score is { } s ? s.ToString("F6", CultureInfo.InvariantCulture) : "")}," +
                $"{(ranks.TryGetValue((p.LeftSourceRecordId, p.RightSourceRecordId), out var r) ? r : "")}," +
                $"{BandName(p.EngineBand)}," +
                $"{(p.WouldBeBand is { } w ? BandName(w) : "")}," +
                $"{(p.IsTrue is { } t ? (t ? "true" : "false") : "")}," +
                $"{(p.Reachable ? "true" : "false")}," +
                $"{(p.Comparable ? "true" : "false")}");
            foreach (var f in fields)
                sb.Append(CultureInfo.InvariantCulture,
                    $",{(sims.TryGetValue(f, out var v) ? v.ToString("F6", CultureInfo.InvariantCulture) : "")}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    internal static string BandName(ScoreBand band) => band switch
    {
        ScoreBand.Auto => "auto",
        ScoreBand.Review => "review",
        ScoreBand.NoMatch => "no-match",
        ScoreBand.NonComparable => "non-comparable",
        ScoreBand.Unreachable => "unreachable",
        _ => band.ToString()
    };
}
