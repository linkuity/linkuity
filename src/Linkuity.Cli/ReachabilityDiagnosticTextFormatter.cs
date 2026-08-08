using System.Globalization;
using System.Text;
using Linkuity.Pipeline;

namespace Linkuity.Cli;

/// <summary>Renders a ReachabilityDiagnosticResult as a human-readable report. No wall-clock or
/// other run-varying content -- see ReachabilityDiagnosticCommands for why that matters.</summary>
public static class ReachabilityDiagnosticTextFormatter
{
    public static string Format(ReachabilityDiagnosticResult result)
    {
        var sb = new StringBuilder();

        sb.AppendLine(CultureInfo.InvariantCulture,
            $"True pairs: {result.TruePairs:N0} ({result.ReachablePairs:N0} reachable, {result.UnreachablePairs:N0} unreachable)");

        AppendCause(sb, "Cause A (every shared key suppressed)", result.CauseA);
        AppendCause(sb, "Cause B1 (declared-Blocking field, no strategy can key it)", result.CauseB1);
        AppendCause(sb, "Cause B2 (undeclared column shares a value)", result.CauseB2);
        AppendCause(sb, "Cause B3 (genuinely disjoint)", result.CauseB3);

        if (result.CauseADetail.Count > 0)
        {
            sb.AppendLine("Cause A detail (strategy, block size -> pairs that would be recovered by raising the cap past this size; buckets overlap, do not sum):");
            foreach (var d in result.CauseADetail)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {d.Strategy}: block size {d.BlockSize} -> {d.PairCount:N0} pair(s)");
        }

        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Normalization implicated: {result.NormalizationImplicated.PairCount:N0} pair(s), " +
            $"{result.NormalizationImplicated.LegalSuffixOnlyPairCount:N0} legal-suffix-only");

        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Field co-occurrence, unreachable pairs (sampled {result.Unreachable.SampledPairCount:N0}):");
        AppendCoOccurrence(sb, result.Unreachable.ByColumn);

        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Field co-occurrence, non-pair control (sampled {result.Control.SampledPairCount:N0}, " +
            $"{result.Control.TruePairsAccidentallyIncluded:N0} true pairs excluded, " +
            $"{result.Control.SelfPairsSkipped:N0} self-pairs skipped):");
        AppendCoOccurrence(sb, result.Control.ByColumn);

        var b = result.Blocks;
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Blocks: {b.TotalBlocks:N0} distinct keys, max block size {b.MaxBlockSize:N0}");
        foreach (var bucket in b.Buckets)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  [{bucket.MinSize}-{bucket.MaxSize}]: {bucket.BlockCount:N0} block(s), {bucket.RecordSlots:N0} record slot(s)");
        sb.AppendLine("Largest blocks:");
        foreach (var lb in b.Largest)
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {lb.Key} (size {lb.Size:N0}) [{lb.Strategy}]");

        return sb.ToString();
    }

    private static void AppendCause(StringBuilder sb, string label, CauseTally cause)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"{label}: {cause.PairCount:N0} pair(s)");
        foreach (var (column, count) in cause.ByColumn)
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {column}: {count:N0}");
    }

    private static void AppendCoOccurrence(StringBuilder sb, IReadOnlyDictionary<string, FieldCoOccurrence> byColumn)
    {
        foreach (var (column, c) in byColumn)
        {
            var lift = c.Lift is { } l ? l.ToString("F2", CultureInfo.InvariantCulture) : "n/a";
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  {column}: {c.SharedCount:N0}/{c.SampleSize:N0} = {c.Rate:P1} " +
                $"[{c.IntervalLow:P1}, {c.IntervalHigh:P1}] lift={lift}");
        }
    }
}
