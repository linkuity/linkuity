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

    /// <summary>Cluster the superseded record was detached from. Null only when the record was in no
    /// active cluster at all — in practice this does not happen for a record ingested through the
    /// normal path, since every current record (including an unmatched singleton) is materialized
    /// into its own 1-member cluster; non-null even when that cluster had only this one member
    /// (see IncrementalResolver.DetachFromCluster's tombstone branch).</summary>
    public Guid? PreviousClusterId { get; init; }

    public required Guid IngestBatchId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
