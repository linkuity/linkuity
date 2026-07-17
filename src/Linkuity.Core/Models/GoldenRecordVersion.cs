namespace Linkuity.Core.Models;

public sealed class GoldenRecordVersion
{
    public required Guid Id { get; init; }
    public required Guid GoldenRecordId { get; init; }
    public required Guid ProjectId { get; init; }
    public required Guid ClusterId { get; init; }
    public required Guid IngestBatchId { get; init; }
    public required int VersionNumber { get; init; }
    public required IReadOnlyDictionary<string, string> Fields { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
