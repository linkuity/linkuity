using System.Globalization;
using System.Text;
using Linkuity.Pipeline;

namespace Linkuity.Cli;

/// <summary>Renders a BlockingAuditResult as a human-readable report.</summary>
public static class BlockingAuditTextFormatter
{
    public static string Format(BlockingAuditResult result)
    {
        var sb = new StringBuilder();

        if (result.Reachability is { } r)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"Recall ceiling: {r.Recall:P1} ({r.ReachablePairs}/{r.TrueMatchPairs} true-match pairs reachable)");
            if (r.MissedPairs.Count > 0)
            {
                sb.AppendLine($"Missed pairs (no shared blocking key): {r.MissedPairs.Count}");
                foreach (var m in r.MissedPairs)
                    sb.AppendLine(CultureInfo.InvariantCulture,
                        $"  [{m.CanonicalKey}] {m.LeftSourceRecordId} vs {m.RightSourceRecordId} | " +
                        $"left={{{string.Join(",", m.LeftKeys)}}} right={{{string.Join(",", m.RightKeys)}}}");
            }
            sb.AppendLine("Per-strategy attribution (pairs reached / uniquely reached):");
            foreach (var a in r.Attribution)
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  {a.StrategyName}: {a.ReachablePairsContributed} / {a.UniquelyReachablePairs}");
        }

        var s = result.Structural;
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Structural: {result.RecordCount} records, {s.TotalBlocks} blocks, {s.TotalCandidatePairs} candidate pairs, " +
            $"{s.SingletonRecordCount} singletons, max block {s.MaxBlockSize}, mean {s.MeanBlockSize:F2}");
        sb.AppendLine("Largest blocks:");
        foreach (var b in s.LargestBlocks)
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {b.Key} (size {b.Size}) [{string.Join(",", b.StrategyNames)}]");

        if (result.CapHazards.Count > 0)
        {
            sb.AppendLine("Cap hazards (block size exceeds --max-candidates):");
            foreach (var b in result.CapHazards)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {b.Key} (size {b.Size})");
        }

        return sb.ToString();
    }

    public static string FormatRecord(RecordBlocking record)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{record.SourceRecordId}:");
        foreach (var (strategy, keys) in record.KeysByStrategy)
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {strategy}: {(keys.Count > 0 ? string.Join(", ", keys) : "(none)")}");
        return sb.ToString().TrimEnd();
    }
}
