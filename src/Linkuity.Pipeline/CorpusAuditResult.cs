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
/// The over-merge invariant: no engine cluster should ever exceed the ORACLE, the largest true
/// entity size found in the supplied ground truth (the largest number of records sharing one
/// canonical key). Unlike cluster pairwise precision — a probabilistic aggregate that a single
/// catastrophic merge can hide inside — this is a hard ceiling: a single cluster over the oracle
/// is enough to fail. Computed fresh from the CURRENT run's own ground truth every time; never a
/// literal number, so it is exactly as correct on a person corpus (FEBRL) as on GLEIF's
/// organization corpus.
/// <para>
/// <see cref="RecordsInClustersOverOracle"/> counts RECORDS, not clusters — a cluster of 500
/// contributes 500 — because it is the headline number later measurements compare a baseline
/// against.
/// </para>
/// <see cref="ClustersOverOneThousand"/> is a reported, non-gated tripwire: a taxonomy whose real
/// entities can legitimately exceed 1,000 members would otherwise be failed for being correct, so
/// only the oracle-relative figures decide <see cref="Passed"/>.
/// </summary>
public sealed record OverMergeAudit(
    int Oracle,
    int LargestClusterSize,
    int ClustersOverOracle,
    long RecordsInClustersOverOracle,
    int ClustersOverOneThousand)
{
    /// <summary>True iff no predicted cluster exceeds the oracle. Vacuously true when the run
    /// carries no ground truth at all (Oracle == 0 and no clusters were measured against it).</summary>
    public bool Passed => ClustersOverOracle == 0;

    /// <summary>Null when <see cref="Passed"/>. Names the oracle, the largest (offending) cluster
    /// size, and the total record count held in oversized clusters — the three quantities the
    /// gate's acceptance criteria require a failure message to name.</summary>
    public string? FailureMessage => Passed ? null :
        $"over-merge gate failed: oracle (largest true entity in ground truth) is {Oracle} " +
        $"record(s); {ClustersOverOracle} cluster(s) exceed it, the largest holding " +
        $"{LargestClusterSize} record(s), for {RecordsInClustersOverOracle} record(s) total " +
        "across all oversized clusters.";
}

/// <summary>
/// Whether this run merged anything it should not have. There is no threshold, because a
/// threshold answers the question "how many wrong merges are acceptable", and the answer is none.
/// <para>
/// The gate this replaces took a declared floor — "at least 99% of merges must be correct". That
/// was the wrong shape twice over. A number typed by whoever runs the report can be set to clear
/// whatever result they just got, which is the same weakness as the relative baseline gate it was
/// meant to improve on; and reporting a wrong-merge count only once it crosses a line means
/// deciding in advance how much silent damage is tolerable.
/// </para>
/// <para>
/// Records that genuinely cannot be told apart from the available fields — legally distinct fund
/// share classes sharing a name and an administrator's address — are not an allowance to merge
/// wrongly. They are a reason not to merge at all: such a pair belongs in review, or apart. So
/// ambiguity in the corpus never raises the ceiling, because there is no ceiling to raise.
/// </para>
/// </summary>
public sealed record WrongMergeGate(long PredictedPositive, long TruePositive)
{
    /// <summary>Merged pairs that ground truth says are different entities.</summary>
    public long WrongMerges => PredictedPositive - TruePositive;

    public bool Passed => WrongMerges == 0;

    /// <summary>
    /// Null when nothing was merged wrongly. Reports the count in pairs rather than as a rate: a
    /// precision of 99% reads as reassuring, and "9,900 pairs of unrelated companies were merged"
    /// does not, though they are the same fact.
    /// </summary>
    public string? FailureMessage => Passed ? null :
        $"wrong-merge gate failed: {WrongMerges} of {PredictedPositive} merged pair(s) join records " +
        "that ground truth says are different entities.";
}

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
    // Never optional, unlike BlastRadius below: the over-merge oracle applies to every profile and
    // every taxonomy unconditionally (no per-taxonomy branch), so there is no "off" state for it to
    // be absent for.
    OverMergeAudit OverMerge,
    // Always present, like OverMerge, and for the same reason: it applies to every profile and
    // every taxonomy unconditionally, so there is no "off" state for it to be absent for.
    WrongMergeGate WrongMerge,
    // Null exactly when the merge policy cannot reject anything under this profile (cohesion off,
    // no size guard) — the same "off means off, not a fabricated zero" shape CorpusAuditService
    // already uses for the `comparisons` list it builds during the walk.
    CohesionBlastRadius? BlastRadius = null);
