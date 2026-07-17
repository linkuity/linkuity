namespace Linkuity.Core.Models;

public sealed class Project
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string ContentType { get; init; }
    public MergeConfiguration? MergeConfiguration { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
