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

public sealed record CorpusAuditResult(
    CorpusAuditInputs Inputs,
    CorpusAuditCounts Counts,
    CorpusAuditMetrics Metrics,
    CorpusAuditClusterSummary ClusterSummary,
    IReadOnlyList<CorpusStratumRow> Strata,
    IReadOnlyList<TruePairOutcome> AllTruePairs);
