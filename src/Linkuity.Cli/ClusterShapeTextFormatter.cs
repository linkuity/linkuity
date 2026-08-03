using System.Globalization;
using System.Text;
using Linkuity.Pipeline;

namespace Linkuity.Cli;

/// <summary>
/// Human-readable cluster-shape report, deterministically ordered so runs diff cleanly.
/// The verdict is the reader's to draw; this prints the two distributions side by side and
/// states plainly what would invalidate the comparison.
/// </summary>
public static class ClusterShapeTextFormatter
{
    public static string Format(ClusterShapeResult result, int top)
    {
        ArgumentNullException.ThrowIfNull(result);

        var sb = new StringBuilder();
        var c = result.Counts;

        sb.AppendLine("=== cluster shape ===");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"records {c.Records:N0}   unlabeled {c.UnlabeledRecords:N0}   " +
            $"clusters {c.Clusters:N0} ({c.MultiRecordClusters:N0} multi-record, {c.Singletons:N0} singleton)");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"auto-merge edges {c.AutoEdges:N0}   candidate pairs {c.CandidatePairs:N0}   " +
            $"pairs compared inside clusters {c.ComparedPairsInsideClusters:N0}");
        sb.AppendLine();

        sb.AppendLine("THE QUESTION: do correct and over-merged clusters have different shapes?");
        sb.AppendLine();
        sb.AppendLine("                              clusters   split by   edges per   compared   largest 2-edge-   agreement");
        sb.AppendLine("verdict      clusters  records   of 3+       1 edge     record    fraction   connected part        rate");
        foreach (var g in result.Groups)
        {
            var split = g.ClustersOfThreeOrMore == 0
                ? "     n/a"
                : g.SplitByOneEdgeFraction.ToString("P1", CultureInfo.InvariantCulture);
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"{Name(g.Verdict),-11} {g.Clusters,8:N0} {g.Records,8:N0} {g.ClustersOfThreeOrMore,8:N0} " +
                $"{split,12} {g.MedianEdgesPerRecord,10:F2} {g.MedianComparedFraction,10:P1} " +
                $"{g.MedianLargestTwoEdgeConnectedFraction,15:P1} {g.MedianAgreementRate,11:P1}");
        }
        sb.AppendLine();
        sb.AppendLine("  'split by 1 edge'  clusters of 3+ that one merge decision holds together — the");
        sb.AppendLine("                     chaining signature. Clusters of 2 are excluded; their only");
        sb.AppendLine("                     edge is trivially a bridge and says nothing.");
        sb.AppendLine("  'compared fraction' share of the pairs inside a cluster the engine actually");
        sb.AppendLine("                     compared. READ THIS FIRST: where it is low, shape is an");
        sb.AppendLine("                     artifact of what blocking retrieved, not of the records.");
        sb.AppendLine("  medians, not means — cluster sizes are heavily skewed.");
        sb.AppendLine();

        sb.AppendLine("  'agreement rate'   of the pairs inside a cluster the engine compared, the share");
        sb.AppendLine("                     it judged to MATCH. The remainder are pairs the engine decided");
        sb.AppendLine("                     were different entities and transitive closure merged anyway.");
        sb.AppendLine();

        sb.AppendLine("by cluster size (a signal that holds only among small clusters is not a signal):");
        sb.AppendLine("size band   verdict     clusters   split by 1   edges/rec   compared   largest 2ec   agreement");
        foreach (var b in result.SizeBands)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"{b.Band,-11} {Name(b.Verdict),-10} {b.Clusters,8:N0} {b.ClustersSplitByOneEdge,12:N0} " +
                $"{b.MedianEdgesPerRecord,11:F2} {b.MedianComparedFraction,10:P1} " +
                $"{b.MedianLargestTwoEdgeConnectedFraction,13:P1} {b.MedianAgreementRate,11:P1}");
        sb.AppendLine();

        sb.AppendLine("COHESION RULE: refuse to auto-merge a cluster whose compared pairs agree less than X.");
        sb.AppendLine("Correct clusters below the line are the cost; over-merged ones are the benefit.");
        sb.AppendLine("             ---- correct (cost) ----      ---- over-merged (benefit) ----");
        sb.AppendLine("     X       clusters      %     records      clusters      %      records");
        foreach (var t in result.AgreementThresholds)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"{t.Threshold,6:P0}   {t.CorrectClustersBelow,10:N0} {t.CorrectClusterFraction,7:P1} " +
                $"{t.CorrectRecordsBelow,10:N0}   {t.OverMergedClustersBelow,11:N0} {t.OverMergedClusterFraction,7:P1} " +
                $"{t.OverMergedRecordsBelow,11:N0}");
        sb.AppendLine();

        sb.AppendLine(CultureInfo.InvariantCulture, $"largest {top} clusters:");
        sb.AppendLine("      size  entities   compared     edges  agreement  bridges  2ec  verdict  representative");
        foreach (var r in result.LargestClusters)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"{r.Size,10:N0} {r.DistinctTrueLabels,9:N0} {r.ComparedPairs,10:N0} {r.AutoEdges,9:N0} " +
                $"{r.AgreementRate,10:P1} {r.BridgeCount,8:N0} " +
                $"{r.LargestTwoEdgeConnectedFraction,4:P0} {Name(r.Verdict),-8} {r.RepresentativeRecordId}");

        return sb.ToString();
    }

    private static string Name(ClusterVerdict verdict) => verdict switch
    {
        ClusterVerdict.Correct => "correct",
        ClusterVerdict.Mixed => "over-merged",
        _ => "unlabeled"
    };
}
