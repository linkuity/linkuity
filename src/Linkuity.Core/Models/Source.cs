namespace Linkuity.Core.Models;

public sealed class Source
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }
    public required string Name { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
