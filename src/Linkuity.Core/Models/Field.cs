namespace Linkuity.Core.Models;

public class Field
{
    public required string Name { get; init; }
    public required SemanticFieldType SemanticType { get; init; }
    public bool ParticipatesInMatching { get; init; } = true;
}
