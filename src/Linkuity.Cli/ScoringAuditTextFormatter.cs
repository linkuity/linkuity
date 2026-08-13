using System.Globalization;
using System.Text;
using Linkuity.Pipeline;

namespace Linkuity.Cli;

/// <summary>Renders a ScoringAuditResult as the human-readable report defined by the spec.</summary>
public static class ScoringAuditTextFormatter
{
    public static string Format(ScoringAuditResult result, int top)
    {
        var sb = new StringBuilder();

        // 1. Header
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Scoring audit: {result.RecordCount} records, similarity={result.SimilarityStrategyName}, " +
            $"scoring={result.ScoringStrategyName}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Thresholds: auto={result.EffectiveAutoThreshold} review={result.EffectiveReviewThreshold}" +
            $"{(result.ThresholdsOverridden ? " (overridden)" : "")}" +
            $"{(result.MaxBlockSize is { } m ? $", maxBlockSize={m}" : "")}");
        sb.AppendLine("Fidelity: batch blocking-linear path (durable/Lucene retrieval not modeled)");
        if (result.Coverage is { } cov)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"Coverage: {cov.LabeledRecordCount}/{cov.RecordCount} records labeled, " +
                $"{cov.SkippedGroundTruthRows} ground-truth rows named absent records, " +
                $"{cov.UnlabeledEndpointPairs} candidate pairs excluded (unlabeled endpoint)");

        // 2. Bands
        var b = result.Bands;
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Bands: auto={b.Auto} review={b.Review} no-match={b.NoMatch} non-comparable={b.NonComparable}");
        if (result.Metrics is { } met)
        {
            // Precision first, and no F1: the objective is precision at the auto threshold, with
            // recall recovered by the review queue rather than traded against it.
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"At auto threshold: precision {Fmt(met.Precision)}  recall {Fmt(met.Recall)}  " +
                $"({met.TruePositives}/{met.PredictedPositives} predicted true, {met.TruePairs} true pairs)");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"Including review:  recall {Fmt(met.RecallIncludingReview)}  " +
                $"(queue holds {met.ReviewPairs} pair(s); review capture {Fmt(met.ReviewCapture)})");
        }

        // 3. Score distribution (0.05 display buckets over candidate pairs)
        var scored = result.Pairs.Where(p => p.Score is not null && p.Comparable).ToList();
        if (scored.Count > 0)
        {
            sb.AppendLine("Score distribution (candidate pairs; true/false/unlabeled per bucket):");
            foreach (var bucket in scored
                .GroupBy(p => Math.Min(19, (int)(p.Score!.Value / 0.05)))
                .OrderBy(g => g.Key))
            {
                var lo = bucket.Key * 0.05;
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  [{lo:F2},{lo + 0.05:F2}) {bucket.Count(p => p.IsTrue == true)}/" +
                    $"{bucket.Count(p => p.IsTrue == false)}/{bucket.Count(p => p.IsTrue is null)}");
            }
        }

        // 4. Sweep
        if (result.Sweep.Count > 0)
        {
            sb.AppendLine("Sweep (cut: predicted / TP / precision / recall):");
            foreach (var row in result.Sweep)
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  {row.Cut:F4}{(row.IsEffectiveThreshold ? " *" : "  ")} {row.PredictedPositives} / " +
                    $"{row.TruePositives} / {Fmt(row.Precision)} / {Fmt(row.Recall)}");
        }

        // 5. Miss decomposition
        if (result.Misses is { } miss)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"Miss decomposition: {miss.TruePairs} true pairs -> auto {miss.AutoMatched}, " +
                $"unreachable {miss.Unreachable}, non-comparable {miss.NonComparable}, " +
                $"review {miss.InReview}, below-review {miss.BelowReview}");

        // 6. Diagnostics
        AppendPairList(sb, $"True pairs below auto (top {top}):", result.TrueBelowAuto.Take(top));
        AppendPairList(sb, $"False pairs at/above review (top {top}):", result.FalseAtOrAboveReview.Take(top));

        return sb.ToString();
    }

    private static void AppendPairList(StringBuilder sb, string title, IEnumerable<ScoredPair> pairs)
    {
        var list = pairs.ToList();
        if (list.Count == 0) return;
        sb.AppendLine(title);
        foreach (var p in list)
        {
            var scoreText = p.Reachable
                ? p.Score!.Value.ToString("F4", CultureInfo.InvariantCulture)
                : $"offline {p.OfflineScore!.Value.ToString("F4", CultureInfo.InvariantCulture)}";
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  {p.LeftSourceRecordId} vs {p.RightSourceRecordId}: {scoreText} [{ScoringAuditCsvFormatter.BandName(p.EngineBand)}" +
                $"{(p.WouldBeBand is { } w ? $" -> would be {ScoringAuditCsvFormatter.BandName(w)}" : "")}]");
            foreach (var c in p.Breakdown)
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"    {c.Signal}: sim {c.Value:F4} x w {c.Weight} -> {c.Contribution:F4}");
        }
    }

    private static string Fmt(double? v)
        => v is { } x ? x.ToString("P1", CultureInfo.InvariantCulture) : "n/a";
}
