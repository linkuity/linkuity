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
        sb.AppendLine("                              clusters   split by   edges per   compared   largest 2-edge-");
        sb.AppendLine("verdict      clusters  records   of 3+       1 edge     record    fraction   connected part");
        foreach (var g in result.Groups)
        {
            var split = g.ClustersOfThreeOrMore == 0
                ? "     n/a"
                : g.SplitByOneEdgeFraction.ToString("P1", CultureInfo.InvariantCulture);
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"{Name(g.Verdict),-11} {g.Clusters,8:N0} {g.Records,8:N0} {g.ClustersOfThreeOrMore,8:N0} " +
                $"{split,12} {g.MedianEdgesPerRecord,10:F2} {g.MedianComparedFraction,10:P1} " +
                $"{g.MedianLargestTwoEdgeConnectedFraction,15:P1}");
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

        sb.AppendLine("by cluster size (a signal that holds only among small clusters is not a signal):");
        sb.AppendLine("size band   verdict     clusters   split by 1   edges/rec   compared   largest 2ec");
        foreach (var b in result.SizeBands)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"{b.Band,-11} {Name(b.Verdict),-10} {b.Clusters,8:N0} {b.ClustersSplitByOneEdge,12:N0} " +
                $"{b.MedianEdgesPerRecord,11:F2} {b.MedianComparedFraction,10:P1} " +
                $"{b.MedianLargestTwoEdgeConnectedFraction,13:P1}");
        sb.AppendLine();

        sb.AppendLine(CultureInfo.InvariantCulture, $"largest {top} clusters:");
        sb.AppendLine("      size  labeled  entities   largest  edges  bridges  compared  2ec  verdict  representative");
        foreach (var r in result.LargestClusters)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"{r.Size,10:N0} {r.LabeledMembers,8:N0} {r.DistinctTrueLabels,9:N0} {r.LargestTrueLabelSize,9:N0} " +
                $"{r.AutoEdges,6:N0} {r.BridgeCount,8:N0} {r.ComparedFraction,9:P1} " +
                $"{r.LargestTwoEdgeConnectedFraction,6:P0} {Name(r.Verdict),-8} {r.RepresentativeRecordId}");

        return sb.ToString();
    }

    private static string Name(ClusterVerdict verdict) => verdict switch
    {
        ClusterVerdict.Correct => "correct",
        ClusterVerdict.Mixed => "over-merged",
        _ => "unlabeled"
    };
}
