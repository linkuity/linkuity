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

    /// <summary>Cluster the deleted record was detached from, if it wasn't already a singleton. Null
    /// when it had no other members to leave behind (nothing to detach).</summary>
    public Guid? PreviousClusterId { get; init; }

    public required Guid IngestBatchId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
