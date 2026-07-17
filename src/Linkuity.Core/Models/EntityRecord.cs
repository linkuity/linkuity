namespace Linkuity.Core.Models;

public sealed class EntityRecord
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
