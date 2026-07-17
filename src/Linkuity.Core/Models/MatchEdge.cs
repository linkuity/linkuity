namespace Linkuity.Core.Models;

public sealed class MatchEdge
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }
    public required Guid IngestBatchId { get; init; }
    public required Guid LeftEntityRecordId { get; init; }
    public required Guid RightEntityRecordId { get; init; }
    public required double Score { get; init; }
    public required string Method { get; init; }

    /// <summary>
    /// The decision band that produced this edge ("auto"). Non-required with a "" default
    /// so durable databases written before Milestone 17 still deserialize.
    /// </summary>
    public string Decision { get; init; } = "";

    /// <summary>
    /// Per-signal score breakdown produced by the matching engine. Non-required with an
    /// empty default for backward compatibility with pre-Milestone-17 databases.
    /// </summary>
    public IReadOnlyList<MatchScoreFactor> Breakdown { get; init; } = [];

    public required DateTimeOffset CreatedAt { get; init; }
}
