namespace Linkuity.Core.Models;

/// <summary>
/// An audit record of a cluster refused by the merge policy, with the numbers that refused it.
/// <para>
/// Dissolution must never be silent. A customer whose established entity splits without a record
/// of why has been handed a worse problem than the over-merge the split prevented.
/// </para>
/// </summary>
public sealed class ClusterDissolutionEvent
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }
    public required IReadOnlyList<Guid> MemberEntityRecordIds { get; init; }

    /// <summary>Set when an ALREADY PUBLISHED cluster was re-evaluated and failed, null when the
    /// cluster never formed. The two are operationally different and must stay distinguishable.</summary>
    public Guid? PreviousClusterId { get; init; }

    public required string Reason { get; init; }
    public required long ComparisonsInside { get; init; }
    public required long AgreementsInside { get; init; }
    public required Guid IngestBatchId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
