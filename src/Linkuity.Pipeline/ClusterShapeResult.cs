namespace Linkuity.Pipeline;

/// <summary>Whether a cluster contains one real entity, several, or nothing labelled.</summary>
public enum ClusterVerdict
{
    /// <summary>Every labelled member belongs to the same real entity.</summary>
    Correct = 0,

    /// <summary>The cluster spans two or more real entities — an over-merge.</summary>
    Mixed = 1,

    /// <summary>No member carries a ground-truth label, so correctness is unknown.</summary>
    Unlabeled = 2
}

/// <summary>
/// One cluster's internal shape. <see cref="ComparedPairs"/> is the load-bearing column: shape
/// can only be read from comparisons the engine actually performed, and it performs far fewer
/// than exist.
/// </summary>
public sealed record ClusterShapeRow(
    string RepresentativeRecordId,
    int Size,
    int LabeledMembers,
    int DistinctTrueLabels,
    int LargestTrueLabelSize,
    int AutoEdges,
    long ComparedPairs,
    int BridgeCount,
    int LargestTwoEdgeConnectedSize,
    ClusterVerdict Verdict)
{
    /// <summary>Pairs inside this cluster that exist at all.</summary>
    public long PossiblePairs => (long)Size * (Size - 1) / 2;

    /// <summary>How much of the cluster the engine actually looked at.</summary>
    public double ComparedFraction => PossiblePairs == 0 ? 0 : (double)ComparedPairs / PossiblePairs;

    public double EdgesPerRecord => Size == 0 ? 0 : (double)AutoEdges / Size;

    /// <summary>Share of the cluster still standing once every single-edge join is cut.</summary>
    public double LargestTwoEdgeConnectedFraction
        => Size == 0 ? 0 : (double)LargestTwoEdgeConnectedSize / Size;

    /// <summary>
    /// Can this cluster be split in two by removing one merge decision? Clusters of two are
    /// excluded: their only edge is trivially a bridge, which says nothing about chaining.
    /// </summary>
    public bool SplitByOneEdge => Size >= 3 && BridgeCount > 0;
}

/// <summary>Shape distribution for one verdict group. Medians, not means — cluster sizes and
/// edge counts are heavily skewed and a mean is dominated by the pathological cases.</summary>
public sealed record ClusterShapeGroupRow(
    ClusterVerdict Verdict,
    int Clusters,
    long Records,
    int LargestCluster,
    int ClustersOfThreeOrMore,
    int ClustersSplitByOneEdge,
    double SplitByOneEdgeFraction,
    double MedianEdgesPerRecord,
    double MedianComparedFraction,
    double MedianLargestTwoEdgeConnectedFraction);

/// <summary>The same shape statistics within one cluster-size band, so a signal that exists only
/// among small clusters cannot masquerade as a signal that holds everywhere.</summary>
public sealed record ClusterShapeSizeBandRow(
    string Band,
    ClusterVerdict Verdict,
    int Clusters,
    int ClustersSplitByOneEdge,
    double MedianEdgesPerRecord,
    double MedianComparedFraction,
    double MedianLargestTwoEdgeConnectedFraction);

public sealed record ClusterShapeCounts(
    int Records,
    int UnlabeledRecords,
    int Clusters,
    int MultiRecordClusters,
    int Singletons,
    long AutoEdges,
    long CandidatePairs,
    long ComparedPairsInsideClusters);

/// <summary>
/// Answers one question: given only the comparisons the engine performs, can correct clusters be
/// told apart from over-merged ones by their internal shape? Reports the two distributions and
/// takes no view — the decision belongs to whoever reads it.
/// </summary>
public sealed record ClusterShapeResult(
    ClusterShapeCounts Counts,
    IReadOnlyList<ClusterShapeGroupRow> Groups,
    IReadOnlyList<ClusterShapeSizeBandRow> SizeBands,
    IReadOnlyList<ClusterShapeRow> LargestClusters);
