namespace Linkuity.Core.Models;

/// <summary>
/// An audit record of a deletion: the source system withdrew a record entirely (e.g. an account
/// closure). <see cref="DeletedEntityRecordId"/> is the row that got tombstoned (kept, never
/// deleted — see EntityRecord.DeletedAt).
/// </summary>
public sealed class RecordDeletedEvent
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }
    public required Guid DeletedEntityRecordId { get; init; }
    public required IReadOnlyDictionary<string, string> PreviousFields { get; init; }

    /// <summary>Cluster the deleted record was detached from. Null only when the record was in no
    /// active cluster at all — in practice this does not happen for a record ingested through the
    /// normal path, since every current record (including an unmatched singleton) is materialized
    /// into its own 1-member cluster; non-null even when that cluster had only this one member
    /// (see IncrementalResolver.DetachFromCluster's tombstone branch).</summary>
    public Guid? PreviousClusterId { get; init; }

    public required Guid IngestBatchId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
