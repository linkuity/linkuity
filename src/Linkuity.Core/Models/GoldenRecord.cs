namespace Linkuity.Core.Models;

public sealed class GoldenRecord
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }
    public required Guid ClusterId { get; init; }
    public required Guid CurrentVersionId { get; init; }
    public required IReadOnlyDictionary<string, string> Fields { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}
