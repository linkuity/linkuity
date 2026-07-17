namespace Linkuity.Core.Models;

/// <summary>
/// An audit record of one cluster being absorbed into a survivor during within-batch
/// bridge-merge. Retained (with the tombstoned cluster's original membership and version
/// history) so a future unmerge could reconstruct the pre-merge state.
/// </summary>
public sealed class ClusterMergeEvent
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }
    public required Guid SurvivorClusterId { get; init; }
    public required Guid AbsorbedClusterId { get; init; }
    public required IReadOnlyList<Guid> AbsorbedMemberEntityRecordIds { get; init; }
    public required IReadOnlyList<Guid> TriggerRecordIds { get; init; }
    public required double Score { get; init; }
    public IReadOnlyList<MatchScoreFactor> Breakdown { get; init; } = [];
    public required Guid IngestBatchId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
