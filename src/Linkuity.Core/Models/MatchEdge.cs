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

    /// <summary>
    /// The scoring strategy that produced <see cref="Score"/>, e.g. "identifier-weighted".
    /// <see cref="Method"/> is not this: it records which ingest path wrote the edge, not what
    /// computed its number.
    /// </summary>
    public string Scorer { get; init; } = "";

    /// <summary>The content type of the profile in force when this edge was scored.</summary>
    public string ProfileContentType { get; init; } = "";

    /// <summary>
    /// Fingerprint of that profile — see <c>ProfileFingerprint</c>. The content type alone
    /// cannot attribute a score, because a profile's thresholds and weights can be edited
    /// without its name changing; two edges can share a name and have been produced by
    /// materially different rules.
    /// </summary>
    public string ProfileFingerprint { get; init; } = "";

    public required DateTimeOffset CreatedAt { get; init; }
}
