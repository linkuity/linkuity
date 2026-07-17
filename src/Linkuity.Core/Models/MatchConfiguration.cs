namespace Linkuity.Core.Models;

public class MatchConfiguration
{
    public required string ContentType { get; init; }
    public required IReadOnlyList<Field> Fields { get; init; }
}
