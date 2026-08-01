namespace Linkuity.Core.Models;

/// <summary>
/// A record so that derived copies use <c>with</c> instead of restating every property. The
/// hand-written copies this replaces are a known defect source: one of them silently dropped a
/// field, and a dropped field here means data loss rather than a compile error.
/// </summary>
public sealed record EntityRecord
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }
    public required Guid SourceId { get; init; }
    public required Guid IngestBatchId { get; init; }
    public required string SourceRecordId { get; init; }
    public required IReadOnlyDictionary<string, string> Fields { get; init; }
    public IReadOnlyList<string> BlockingKeys { get; init; } = [];
    public required DateTimeOffset CreatedAt { get; init; }
}
