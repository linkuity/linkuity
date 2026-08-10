using System.Globalization;
using System.Text;
using Linkuity.Pipeline;

namespace Linkuity.Cli;

/// <summary>Diffable corpus audit output: one metric or stratum per row, stable order.</summary>
public static class CorpusAuditCsvFormatter
{
    public static string Format(CorpusAuditResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var sb = new StringBuilder();
        sb.AppendLine("section,key,value");

        void Row(string section, string key, object value)
            => sb.AppendLine(CultureInfo.InvariantCulture, $"{section},{key},{value}");

        var c = result.Counts;
        Row("count", "records", c.Records);
        Row("count", "unlabeled_records", c.UnlabeledRecordCount);
        Row("count", "unlabeled_endpoint_pairs", c.UnlabeledEndpointPairs);
        Row("count", "true_pairs", c.TruePairs);
        Row("count", "candidate_pairs", c.CandidatePairs);
        Row("count", "candidate_pair_occurrences", c.CandidatePairOccurrences);
        Row("count", "actual_positive", c.ActualPositive);
        Row("count", "predicted_positive", c.PredictedPositive);
        Row("count", "true_positive", c.TruePositive);
        Row("count", "reachable_true_pairs", c.ReachableTruePairs);
        Row("count", "direct_auto_true_pairs", c.DirectAutoTruePairs);
        Row("count", "floor_lifted_pairs", c.FloorLiftedPairs);
        Row("count", "golden_records", result.ClusterSummary.GoldenRecordCount);
        Row("count", "unified_clusters", result.ClusterSummary.UnifiedClusterCount);
        Row("count", "singletons", result.ClusterSummary.SingletonCount);
        Row("count", "largest_cluster", result.ClusterSummary.LargestClusterSize);

        var om = result.OverMerge;
        Row("over_merge", "oracle", om.Oracle);
        Row("over_merge", "largest_cluster", om.LargestClusterSize);
        Row("over_merge", "clusters_over_oracle", om.ClustersOverOracle);
        Row("over_merge", "records_in_clusters_over_oracle", om.RecordsInClustersOverOracle);
        Row("over_merge", "clusters_over_1000", om.ClustersOverOneThousand);
        Row("over_merge", "passed", om.Passed);

        var m = result.Metrics;
        Row("metric", "reachability", m.Reachability.ToString("F6", CultureInfo.InvariantCulture));
        Row("metric", "direct_auto_recall", m.DirectAutoRecall.ToString("F6", CultureInfo.InvariantCulture));
        Row("metric", "post_cluster_pairwise_recall",
            m.PostClusterPairwiseRecall.ToString("F6", CultureInfo.InvariantCulture));
        Row("metric", "cluster_pairwise_precision",
            m.ClusterPairwisePrecision.ToString("F6", CultureInfo.InvariantCulture));

        foreach (var f in result.Inputs.FieldCoverage)
            Row("coverage", f.FieldName, f.PairsPopulatedBothSides);

        foreach (var s in result.Strata.OrderBy(s => s.Id))
        {
            Row("stratum", $"{s.Id}.true_pairs", s.TruePairs);
            Row("stratum", $"{s.Id}.reachable", s.Reachable);
            Row("stratum", $"{s.Id}.auto", s.Auto);
            Row("stratum", $"{s.Id}.review", s.Review);
            Row("stratum", $"{s.Id}.no_match", s.NoMatch);
            Row("stratum", $"{s.Id}.non_comparable", s.NonComparable);
            Row("stratum", $"{s.Id}.post_cluster_true_positive", s.PostClusterTruePositive);
        }

        // Present only when the merge policy could reject something (spec §6.4). Absent, not
        // zero-filled, when cohesion is off — a CSV row of zeros would read as "measured and found
        // nothing" rather than "not measured".
        if (result.BlastRadius is { } br)
        {
            Row("cohesion_blast_radius", "rejected_components", br.RejectedComponents);
            Row("cohesion_blast_radius", "components_containing_a_lost_correct_cluster",
                br.ComponentsContainingALostCorrectCluster);
            Row("cohesion_blast_radius", "correct_clusters_lost", br.CorrectClustersLost);
            Row("cohesion_blast_radius", "records_in_lost_correct_clusters", br.RecordsInLostCorrectClusters);
        }
        return sb.ToString();
    }
}
