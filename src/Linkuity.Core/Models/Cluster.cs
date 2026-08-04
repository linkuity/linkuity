namespace Linkuity.Core.Models;

public sealed class Cluster
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }
    public required IReadOnlyList<Guid> MemberEntityRecordIds { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// "active" or "merged". Non-required default for pre-M22 databases. "merged" is this schema's
    /// only "not active" marker and is reused for two different retirements: a cluster ABSORBED by
    /// a survivor (<see cref="MergedIntoClusterId"/> set — see <c>ClusterMergeEvent</c>) and a
    /// cluster the merge policy DISSOLVED (<see cref="MergedIntoClusterId"/> null — see
    /// <c>ClusterDissolutionEvent</c>). The two are told apart by that field, not by a second status.
    /// </summary>
    public string Status { get; init; } = "active";

    /// <summary>Set only for an absorption tombstone: the surviving cluster this was merged into.
    /// Null for an active cluster AND for a dissolution tombstone — the two "merged, no survivor"
    /// cases are distinguished by which audit event references this Id, not by this field.</summary>
    public Guid? MergedIntoClusterId { get; init; }

    /// <summary>
    /// Comparisons the engine made between two members of THIS cluster, across all ingests.
    /// The denominator of cohesion. Counts confident rejections as well as agreements — without
    /// them a cluster whose records disagree is indistinguishable from one never looked inside.
    /// </summary>
    public long ComparisonsInside { get; init; }

    /// <summary>How many of those comparisons scored in the auto band.</summary>
    public long AgreementsInside { get; init; }
}
