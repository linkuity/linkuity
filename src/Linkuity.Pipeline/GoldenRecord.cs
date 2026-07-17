namespace Linkuity.Pipeline;

public class GoldenRecord
{
    public required Guid ClusterId { get; init; }
    public required IReadOnlyList<string> MemberIds { get; init; }
    public required IReadOnlyDictionary<string, string> Fields { get; init; }
}
