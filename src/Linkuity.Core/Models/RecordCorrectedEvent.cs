namespace Linkuity.Core.Models;

/// <summary>
/// An audit record of a correction: a resend of an existing (project, source_record_id) whose
/// field values changed. <see cref="SupersededEntityRecordId"/> is the old, now-superseded row
/// (kept, never deleted — see EntityRecord.SupersededAt); <see cref="CorrectedEntityRecordId"/> is
/// the new row that replaced it. The ingest caller never knows the old record's internal Id, so a
/// correction always produces a new one rather than reusing the old.
/// </summary>
public sealed class RecordCorrectedEvent
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }
    public required Guid SupersededEntityRecordId { get; init; }
    public required Guid CorrectedEntityRecordId { get; init; }
    public required IReadOnlyDictionary<string, string> PreviousFields { get; init; }
    public required IReadOnlyDictionary<string, string> NewFields { get; init; }

    /// <summary>Cluster the superseded record was detached from, if it wasn't already a singleton. Null
    /// when it had no other members to leave behind (nothing to detach).</summary>
    public Guid? PreviousClusterId { get; init; }

    public required Guid IngestBatchId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
