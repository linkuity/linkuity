namespace Linkuity.Pipeline;

public enum CorpusBand { Auto, Review, NoMatch, NonComparable }

/// <summary>Name-similarity relationship between a true pair's canonical token sets.
/// Frozen into the baseline (spec §8.1) because the canonicalizer under test computes it.</summary>
public enum Stratum { S1Identical, S2Containment, S3StrongOverlap, S4WeakOverlap, S5Disjoint }

/// <summary>How many true pairs had each matchable field populated on BOTH sides. Replaces a
/// single "effective denominator" scalar, which was wrong on any corpus where field coverage
/// varies from pair to pair (the showcase populates address and postal; SEC does not).</summary>
public sealed record FieldCoverageRow(string FieldName, double Weight, long PairsPopulatedBothSides);

public sealed record CorpusAuditInputs(
    int? EffectiveMaxBlockSize,
    double AutoMatchThreshold,
    double ReviewThreshold,
    double ReviewFloorGate,
    IReadOnlyList<FieldCoverageRow> FieldCoverage);

public sealed record CorpusAuditCounts(
    int Records,
    int UnlabeledRecordCount,
    long UnlabeledEndpointPairs,
    long TruePairs,
    long CandidatePairs,
    long CandidatePairOccurrences,
    long ActualPositive,
    long PredictedPositive,
    long TruePositive,
    long ReachableTruePairs,
    long DirectAutoTruePairs,
    long FloorLiftedPairs);

public sealed record CorpusAuditMetrics(
    double Reachability,
    double DirectAutoRecall,
    double PostClusterPairwiseRecall,
    double ClusterPairwisePrecision);

public sealed record CorpusStratumRow(
    Stratum Id,
    long TruePairs,
    long Reachable,
    long Auto,
    long Review,
    long NoMatch,
    long NonComparable,
    long PostClusterTruePositive)
{
    /// <summary>Null (rendered "n/a") rather than 0.0 when the stratum is empty.</summary>
    public double? PostClusterPairwiseRecall => TruePairs == 0 ? null : (double)PostClusterTruePositive / TruePairs;
}

public sealed record TruePairOutcome(
    string LeftSourceRecordId,
    string RightSourceRecordId,
    Stratum Stratum,
    bool Reachable,
    CorpusBand? Band,
    double? Score,
    bool SameCluster);

/// <summary>Distinct predicted clusters — "golden records" in showcase vocabulary.</summary>
public sealed record CorpusAuditClusterSummary(
    int GoldenRecordCount, int LargestClusterSize, int UnifiedClusterCount, int SingletonCount);

/// <summary>
/// What cluster-cohesion rejection destroyed, spec §6.4/§9.7: "the re-run must report how often a
/// failing component contains previously-correct clusters. If that number is large, peel-back is
/// worth revisiting despite its order-dependence." The design in play is reject-wholesale — a
/// component that fails cohesion dissolves to singletons in full, so a true entity whose entire
/// membership happened to land inside that component is destroyed along with whatever else made
/// the component fail. <see cref="RejectedComponents"/> counts only
/// <see cref="Linkuity.Matching.Clustering.ClusterMergeVerdict.RejectedForCohesion"/> — this is a
/// cohesion measurement, not a size-guard one, and <c>MaxAutoClusterSize</c> is off throughout
/// stage 1b.
/// </summary>
public sealed record CohesionBlastRadius(
    long RejectedComponents,
    long ComponentsContainingALostCorrectCluster,
    long CorrectClustersLost,
    long RecordsInLostCorrectClusters);

public sealed record CorpusAuditResult(
    CorpusAuditInputs Inputs,
    CorpusAuditCounts Counts,
    CorpusAuditMetrics Metrics,
    CorpusAuditClusterSummary ClusterSummary,
    IReadOnlyList<CorpusStratumRow> Strata,
    IReadOnlyList<TruePairOutcome> AllTruePairs,
    // Null exactly when the merge policy cannot reject anything under this profile (cohesion off,
    // no size guard) — the same "off means off, not a fabricated zero" shape CorpusAuditService
    // already uses for the `comparisons` list it builds during the walk.
    CohesionBlastRadius? BlastRadius = null);
