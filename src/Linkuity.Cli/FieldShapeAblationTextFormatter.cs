using System.Globalization;
using System.Text;
using Linkuity.Pipeline;

namespace Linkuity.Cli;

/// <summary>
/// Human-readable field-shape ablation report. The point of the exercise is one number per
/// width — the recall achieved at the cut where direct-edge precision first reaches 100% — so
/// that number gets its own column rather than being buried in a sweep dump. A width where no
/// cut reaches 100% precision prints "unreachable" in that column, never a substituted "closest"
/// value, per the design constraint this instrument exists to honor.
/// </summary>
public static class FieldShapeAblationTextFormatter
{
    public static string Format(FieldShapeAblationResult result, string scoringStrategy)
    {
        ArgumentNullException.ThrowIfNull(result);

        var sb = new StringBuilder();
        sb.AppendLine("=== field-shape ablation ===");
        sb.AppendLine(CultureInfo.InvariantCulture, $"scoring strategy: {scoringStrategy}");
        sb.AppendLine();
        sb.AppendLine("THE QUESTION: does the usable (100%-precision) threshold move as matchable field count changes?");
        sb.AppendLine();
        sb.AppendLine("width                matchable  true pairs  reachability  threshold@100%P    recall there");
        foreach (var r in result.Rows)
        {
            var threshold = r.ThresholdAt100Precision is { } t ? t.ToString("F4", CultureInfo.InvariantCulture) : "unreachable";
            var recall = r.PerfectPrecisionReachable && r.RecallAt100Precision is { } rc
                ? rc.ToString("P1", CultureInfo.InvariantCulture)
                : "n/a";
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"{r.WidthName,-20} {r.MatchableFieldCount,9:N0} {r.TruePairs,11:N0} {r.Reachability,12:P1} " +
                $"{threshold,16} {recall,15}");
        }
        sb.AppendLine();

        var unreachableRows = result.Rows.Where(r => !r.PerfectPrecisionReachable).ToList();
        if (unreachableRows.Count > 0)
        {
            sb.AppendLine("100% precision is NOT reachable at ANY threshold for the following width(s) " +
                          "(reporting the closest precision observed instead would misrepresent them as comparable):");
            foreach (var r in unreachableRows)
            {
                var bestP = r.MaxPrecisionObserved is { } p ? p.ToString("P1", CultureInfo.InvariantCulture) : "n/a";
                var bestR = r.RecallAtMaxPrecisionObserved is { } rc ? rc.ToString("P1", CultureInfo.InvariantCulture) : "n/a";
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  {r.WidthName}: best precision observed {bestP} (recall {bestR} there)");
            }
            sb.AppendLine();
        }

        var reachable = result.Rows.Where(r => r.PerfectPrecisionReachable).ToList();
        if (reachable.Count > 0)
        {
            var min = reachable.Min(r => r.ThresholdAt100Precision!.Value);
            var max = reachable.Max(r => r.ThresholdAt100Precision!.Value);
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"threshold@100%P range across widths with a reachable cut: {min:F4} .. {max:F4} " +
                $"(spread {max - min:F4})");
        }

        return sb.ToString();
    }
}
