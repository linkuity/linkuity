using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Clustering;

/// <summary>
/// Rejects a cluster its own comparisons contradict, plus an optional size backstop.
/// <para>
/// Measured on 1,052,432 labelled SEC records: at a 0.60 threshold this rejects 6,606 over-merged
/// clusters covering 124,034 records at a cost of ZERO correct clusters out of 11,786. The
/// 29,477-record cluster agrees on 54.2% of the pairs compared inside it and is caught.
/// </para>
/// <para>
/// <see cref="MatchingProfile.MinClusterCohesion"/> is off (null) by default in this stage.
/// "Disabled" is handled here, not as a special case at either call site — every caller of this
/// policy gets it for free, and there is exactly one place that has to know cohesion can be off.
/// </para>
/// </summary>
public sealed class CohesionClusterMergePolicy : IClusterMergePolicy
{
    public string Name => "cohesion";

    /// <summary>Mirrors the two guards Evaluate below actually reads: with both null, the
    /// cohesion check and the size check are each skipped, so nothing this policy does can ever
    /// return a non-Accepted verdict.</summary>
    public bool CanReject(MatchingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.MinClusterCohesion is not null || profile.MaxAutoClusterSize is not null;
    }

    public ClusterMergeVerdict Evaluate(ClusterEvidenceCounts counts, MatchingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // Cohesion first, so a cluster that is both incoherent AND oversized reports the reason
        // that actually describes what is wrong with it. Null means the check is off; the size
        // guard below still applies regardless.
        if (profile.MinClusterCohesion is { } minCohesion && counts.AgreementRate < minCohesion)
            return ClusterMergeVerdict.RejectedForCohesion;

        if (profile.MaxAutoClusterSize is { } max && counts.Members > max)
            return ClusterMergeVerdict.RejectedForSize;

        return ClusterMergeVerdict.Accepted;
    }
}
