using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Clustering;

/// <summary>
/// Rejects a cluster its own comparisons contradict, plus an optional size backstop.
/// <para>
/// Measured on 1,052,432 labelled SEC records: at a 0.60 threshold this rejects 6,606 over-merged
/// clusters covering 124,034 records at a cost of ZERO correct clusters out of 11,786. The
/// 29,477-record cluster agrees on 54.2% of the pairs compared inside it and is caught.
/// </para>
/// </summary>
public sealed class CohesionClusterMergePolicy : IClusterMergePolicy
{
    public string Name => "cohesion";

    public ClusterMergeVerdict Evaluate(ClusterEvidenceCounts counts, MatchingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // Cohesion first, so a cluster that is both incoherent AND oversized reports the reason
        // that actually describes what is wrong with it.
        if (counts.AgreementRate < profile.MinClusterCohesion)
            return ClusterMergeVerdict.RejectedForCohesion;

        if (profile.MaxAutoClusterSize is { } max && counts.Members > max)
            return ClusterMergeVerdict.RejectedForSize;

        return ClusterMergeVerdict.Accepted;
    }
}
