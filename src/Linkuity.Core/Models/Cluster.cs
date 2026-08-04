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

    /// <summary>
    /// Comparisons the engine made between two members of THIS cluster, across all ingests.
    /// The denominator of cohesion. Counts confident rejections as well as agreements — without
    /// them a cluster whose records disagree is indistinguishable from one never looked inside.
    /// </summary>
    public long ComparisonsInside { get; init; }

    /// <summary>How many of those comparisons scored in the auto band.</summary>
    public long AgreementsInside { get; init; }
}
