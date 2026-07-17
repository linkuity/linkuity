namespace Linkuity.Core.Models;

public sealed class Cluster
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }
    public required IReadOnlyList<Guid> MemberEntityRecordIds { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>"active" or "merged". Non-required default for pre-M22 databases.</summary>
    public string Status { get; init; } = "active";

    /// <summary>When Status == "merged", the surviving cluster this was absorbed into.</summary>
    public Guid? MergedIntoClusterId { get; init; }
}
