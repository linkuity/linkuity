using System.Globalization;
using System.Text;
using Linkuity.Pipeline;

namespace Linkuity.Cli;

/// <summary>Human-readable corpus audit report, deterministically ordered so runs diff cleanly.</summary>
public static class CorpusAuditTextFormatter
{
    public static string Format(CorpusAuditResult result, int top)
    {
        ArgumentNullException.ThrowIfNull(result);

        var sb = new StringBuilder();
        var c = result.Counts;
        var m = result.Metrics;
        var i = result.Inputs;

        sb.AppendLine("=== corpus audit ===");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"records {c.Records:N0}   unlabeled {c.UnlabeledRecordCount:N0}   true pairs {c.TruePairs:N0}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"candidate pairs {c.CandidatePairs:N0} emitted from {c.CandidatePairOccurrences:N0} block-pair visits");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"maxBlockSize {i.EffectiveMaxBlockSize?.ToString(CultureInfo.InvariantCulture) ?? "off"} (effective)   " +
            $"auto {i.AutoMatchThreshold}   review {i.ReviewThreshold}   reviewFloorGate {i.ReviewFloorGate}");

        // FloorLiftedPairs counts ANY floor lifting the final score above the raw weighted average
        // — the identifier floor (0.98) as well as the review floor — so it must not be labelled
        // review-floor-specific.
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"pairs whose score was lifted by a floor rather than the weighted average: {c.FloorLiftedPairs:N0}");
        if (c.UnlabeledEndpointPairs > 0)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"unlabeled endpoint pairs (excluded from metrics): {c.UnlabeledEndpointPairs:N0}");
        sb.AppendLine();

        sb.AppendLine("field coverage over true pairs (both sides populated):");
        foreach (var f in i.FieldCoverage)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  {f.FieldName,-24} weight {f.Weight,5}   {f.PairsPopulatedBothSides:N0} pair(s)");

        // Driven by the ACTUAL coverage data. This instrument is corpus-agnostic: a hardcoded
        // "name only" claim would be wrong on any corpus that populates more fields.
        var populated = i.FieldCoverage.Where(f => f.PairsPopulatedBothSides > 0).ToList();
        if (i.FieldCoverage.Count > 0 && populated.Count == 0)
            sb.AppendLine("NOTE: no matchable field is populated on both sides of any true pair, so every " +
                          "true pair is non-comparable and the figures below carry no scoring signal.");
        else if (populated.Count == 1)
            sb.AppendLine($"NOTE: {populated[0].FieldName} is the only field populated on both sides of " +
                          "any true pair, so the score IS that field's similarity. These figures are not " +
                          "comparable with a corpus where more fields are present.");
        sb.AppendLine();

        sb.AppendLine(CultureInfo.InvariantCulture,
            $"reachability                  {m.Reachability:P2}  ({c.ReachableTruePairs:N0}/{c.TruePairs:N0})");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"direct auto recall            {m.DirectAutoRecall:P2}  ({c.DirectAutoTruePairs:N0}/{c.TruePairs:N0})");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"post-cluster pairwise recall  {m.PostClusterPairwiseRecall:P2}  ({c.TruePositive:N0}/{c.ActualPositive:N0})");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"cluster pairwise precision    {m.ClusterPairwisePrecision:P2}  ({c.TruePositive:N0}/{c.PredictedPositive:N0})");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"golden records {result.ClusterSummary.GoldenRecordCount:N0}   " +
            $"unified {result.ClusterSummary.UnifiedClusterCount:N0}   " +
            $"singletons {result.ClusterSummary.SingletonCount:N0}   " +
            $"largest cluster {result.ClusterSummary.LargestClusterSize:N0}");
        sb.AppendLine();

        // Over-merge oracle: the largest true entity size FOUND IN THIS RUN'S OWN GROUND TRUTH,
        // never a hardcoded number. RecordsInClustersOverOracle is the headline figure later
        // measurements compare a baseline against, so it is named explicitly rather than folded
        // into the cluster-summary line above.
        var om = result.OverMerge;
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"over-merge oracle (largest true entity in ground truth): {om.Oracle:N0}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"  largest cluster {om.LargestClusterSize:N0}   " +
            $"clusters over oracle {om.ClustersOverOracle:N0}   " +
            $"records in clusters over oracle {om.RecordsInClustersOverOracle:N0}   " +
            $"clusters over 1,000 {om.ClustersOverOneThousand:N0}");
        sb.AppendLine(om.Passed ? "over-merge gate: PASS" : $"over-merge gate: FAIL -- {om.FailureMessage}");

        // "not gated" is deliberately not "PASS": a floor nobody declared has not been met, and
        // reporting it as passing is how an ungated run comes to be cited as a safe one.
        var mp = result.MergePrecision;
        sb.AppendLine(!mp.Evaluated
            ? $"merge-precision gate: not gated (no --min-merge-precision declared); " +
              $"{mp.WrongMerges} of {mp.PredictedPositive} merged pair(s) are wrong"
            : mp.Passed
                ? $"merge-precision gate: PASS ({mp.Precision:P4} >= {mp.Floor:P4}, {mp.WrongMerges} wrong merge(s))"
                : $"merge-precision gate: FAIL -- {mp.FailureMessage}");
        sb.AppendLine();

        sb.AppendLine("stratum          true    reach     auto   review  nomatch  noncomp   recall");
        foreach (var s in result.Strata.OrderBy(s => s.Id))
        {
            // "n/a" rather than 0.00% for an empty stratum: an absent cohort must never read as a
            // total miss.
            var recall = s.PostClusterPairwiseRecall is { } r ? r.ToString("P2", CultureInfo.InvariantCulture) : "n/a";
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"{s.Id,-14} {s.TruePairs,7:N0} {s.Reachable,8:N0} {s.Auto,8:N0} {s.Review,8:N0} " +
                $"{s.NoMatch,8:N0} {s.NonComparable,8:N0} {recall,8}");
        }

        if (result.BlastRadius is { } br)
        {
            sb.AppendLine();
            sb.AppendLine("cohesion blast radius (spec section 6.4 - reject-wholesale destroys any correct");
            sb.AppendLine("sub-cluster inside a rejected component, not only the contradiction that caused it):");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  components rejected for cohesion         {br.RejectedComponents:N0}");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  of those, containing a lost correct cluster {br.ComponentsContainingALostCorrectCluster:N0}");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  correct clusters lost                     {br.CorrectClustersLost:N0}");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  records in those lost clusters             {br.RecordsInLostCorrectClusters:N0}");
        }

        var missed = result.AllTruePairs.Where(p => !p.SameCluster)
            .OrderBy(p => p.Stratum)
            .ThenBy(p => p.LeftSourceRecordId, StringComparer.Ordinal)
            .ThenBy(p => p.RightSourceRecordId, StringComparer.Ordinal)
            .Take(top).ToList();
        if (missed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"missed true pairs (first {missed.Count}):");
            foreach (var e in missed)
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  {e.Stratum,-14} {e.LeftSourceRecordId} | {e.RightSourceRecordId}  " +
                    $"reachable={e.Reachable,-5} band={e.Band?.ToString() ?? "-",-13} " +
                    $"score={e.Score?.ToString("F4", CultureInfo.InvariantCulture) ?? "-"}");
        }
        return sb.ToString();
    }
}
