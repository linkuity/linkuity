namespace Linkuity.Core.Models;

public sealed class IngestBatch
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }
    public required Guid SourceId { get; init; }
    public Guid? JobId { get; init; }
    public required int RecordCount { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
