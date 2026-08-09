using System.Globalization;
using System.Text;
using Linkuity.Pipeline;

namespace Linkuity.Cli;

/// <summary>Renders a BlockingAuditResult as machine-readable rows for diffing runs.</summary>
public static class BlockingAuditCsvFormatter
{
    public static string Format(BlockingAuditResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("section,key,size,strategies");
        foreach (var b in result.Blocks)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"block,{Escape(b.Key)},{b.Size},{Escape(string.Join("|", b.StrategyNames))}");
        foreach (var b in result.CapHazards)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"cap_hazard,{Escape(b.Key)},{b.Size},{Escape(string.Join("|", b.StrategyNames))}");

        if (result.Suppression is { } sup)
            foreach (var b in sup.SuppressedBlocks)
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"suppressed,{Escape(b.Key)},{b.Size},{Escape(string.Join("|", b.StrategyNames))}");

        if (result.Reachability is { } r)
        {
            sb.AppendLine();
            // population_count and sampled_count ride on EVERY row: the pair rows are a
            // deterministic sample capped at BlockingAuditService.MissedPairSampleCap, and without
            // the population on the row a reader counting 500 rows would read 500 as the answer.
            // They are constant down a section by design -- a marker, not a per-row measurement.
            sb.AppendLine("section,left,right,canonical,left_keys,right_keys,population_count,sampled_count");
            foreach (var m in r.MissedPairs)
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"missed,{Escape(m.LeftSourceRecordId)},{Escape(m.RightSourceRecordId)},{Escape(m.CanonicalKey)}," +
                    $"{Escape(string.Join("|", m.LeftKeys))},{Escape(string.Join("|", m.RightKeys))}," +
                    $"{r.MissedPairCount},{r.MissedPairs.Count}");
            if (result.Suppression is { EffectiveReachability: { } er })
            {
                var lost = BlockingAuditTextFormatter.LostToSuppression(r, er);
                var lostCount = BlockingAuditTextFormatter.LostToSuppressionCount(r, er);
                foreach (var m in lost)
                    sb.AppendLine(CultureInfo.InvariantCulture,
                        $"suppression_missed,{Escape(m.LeftSourceRecordId)},{Escape(m.RightSourceRecordId)},{Escape(m.CanonicalKey)}," +
                        $"{Escape(string.Join("|", m.LeftKeys))},{Escape(string.Join("|", m.RightKeys))}," +
                        $"{lostCount},{lost.Count}");
            }
        }
        return sb.ToString();
    }

    private static string Escape(string value)
        => value.Contains(',') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}
