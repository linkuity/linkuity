namespace Linkuity.Core.Models;

public sealed class ReviewTask
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }
    public required Guid IngestBatchId { get; init; }
    public required Guid NewEntityRecordId { get; init; }
    public required Guid CandidateEntityRecordId { get; init; }
    public required double Score { get; init; }
    public required string Reason { get; init; }
    public required string Status { get; init; }

    /// <summary>
    /// Per-signal score breakdown for the uncertain pair. Non-required with an empty
    /// default for backward compatibility with pre-Milestone-17 databases.
    /// </summary>
    public IReadOnlyList<MatchScoreFactor> Breakdown { get; init; } = [];

    /// <summary>For cluster_merge_suggestion reviews: the cluster the new record joined.</summary>
    public Guid? LeftClusterId { get; init; }

    /// <summary>For cluster_merge_suggestion reviews: the other implicated cluster.</summary>
    public Guid? RightClusterId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
