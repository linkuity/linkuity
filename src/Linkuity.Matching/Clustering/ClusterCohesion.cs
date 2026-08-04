namespace Linkuity.Matching.Clustering;

/// <summary>
/// What the engine's own comparisons say about one cluster it formed.
/// <para>
/// <see cref="ComparisonsInside"/> counts pairs inside the cluster the engine actually compared —
/// not pairs that exist. It compares a small fraction of them at corpus scale, so the rate is an
/// estimate from a sample, and a contradiction never evaluated cannot be detected. This is a lower
/// bound on disagreement, not a complete picture.
/// </para>
/// </summary>
public readonly record struct ClusterEvidenceCounts(int Members, long ComparisonsInside, long AgreementsInside)
{
    /// <summary>
    /// Of the pairs compared inside this cluster, the share judged to match. The complement is the
    /// cluster's own contradiction: pairs the engine decided were different entities and
    /// transitive closure merged regardless.
    /// <para>
    /// A cluster with nothing compared inside it returns 1: it has not contradicted itself, and
    /// reading "no evidence" as "total disagreement" would reject clusters for the sin of being
    /// under-compared.
    /// </para>
    /// </summary>
    public double AgreementRate => ComparisonsInside == 0 ? 1.0 : (double)AgreementsInside / ComparisonsInside;
}

/// <summary>What a merge policy decided about a cluster, and why.
/// Named ClusterMergeVerdict, not ClusterVerdict: <c>Linkuity.Pipeline.ClusterVerdict</c> already
/// exists and means something entirely different — whether ground truth says a cluster is correct.
/// Pipeline references Matching, so two same-named enums would force qualification wherever the
/// audit reasons about both.</summary>
public enum ClusterMergeVerdict
{
    Accepted = 0,

    /// <summary>The engine's own comparisons inside the cluster contradict it too often.</summary>
    RejectedForCohesion,

    /// <summary>The cluster is larger than the profile permits to form automatically.</summary>
    RejectedForSize
}
