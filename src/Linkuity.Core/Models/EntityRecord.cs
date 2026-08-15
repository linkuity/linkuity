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

    /// <summary>
    /// Null: this is the current, live record for its (project, source_record_id). Non-null: a
    /// later correction replaced it with a new record (fresh Id — the ingest caller never knows
    /// this record's internal Id, so a correction cannot reuse it). The row is kept, never deleted,
    /// so history (MatchEdge, GoldenRecordVersion snapshots) that references this Id stays valid.
    /// </summary>
    public DateTimeOffset? SupersededAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
