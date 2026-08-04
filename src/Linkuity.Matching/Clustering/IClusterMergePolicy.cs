using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Clustering;

/// <summary>
/// Decides whether a cluster the engine formed is allowed to stand.
/// <para>
/// Consulted AFTER component formation and never during union. Evaluating mid-union would make
/// the answer depend on the order edges arrive in, which the batch path does not have — and that
/// order dependence is precisely what disqualified the mechanism this replaces.
/// </para>
/// </summary>
public interface IClusterMergePolicy
{
    string Name { get; }

    ClusterMergeVerdict Evaluate(ClusterEvidenceCounts counts, MatchingProfile profile);
}
