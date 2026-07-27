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

        if (result.Suppression is { } sup)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"Suppressed keys (corpus frequency > {sup.MaxBlockSize}): {sup.SuppressedBlocks.Count}");
            foreach (var b in sup.SuppressedBlocks)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {b.Key} (size {b.Size}) [{string.Join(",", b.StrategyNames)}]");
            if (sup.NoActiveKeyRecordIds.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"Records with no active keys (blocking singletons): {sup.NoActiveKeyRecordIds.Count}");
                foreach (var id in sup.NoActiveKeyRecordIds)
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  {id}");
            }
            if (sup.EffectiveReachability is { } er && result.Reachability is { } raw)
            {
                var lost = LostToSuppression(raw, er);
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"Effective recall ceiling: {er.Recall:P1} ({er.ReachablePairs}/{er.TrueMatchPairs}) - pairs lost to suppression: {lost.Count}");
                foreach (var m in lost)
                    sb.AppendLine(CultureInfo.InvariantCulture,
                        $"  [{m.CanonicalKey}] {m.LeftSourceRecordId} vs {m.RightSourceRecordId} | " +
                        $"left={{{string.Join(",", m.LeftKeys)}}} right={{{string.Join(",", m.RightKeys)}}}");
            }
        }

        return sb.ToString();
    }

    /// <summary>Pairs missed under suppression that the raw key sets reached: suppression's recall cost.</summary>
    public static IReadOnlyList<MissedPair> LostToSuppression(BlockingReachabilityReport raw, BlockingReachabilityReport effective)
    {
        var rawMissed = raw.MissedPairs
            .Select(m => (m.LeftSourceRecordId, m.RightSourceRecordId))
            .ToHashSet();
        return effective.MissedPairs
            .Where(m => !rawMissed.Contains((m.LeftSourceRecordId, m.RightSourceRecordId)))
            .ToList(); // already deterministic: ComputeReachability sorts missed pairs
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
