using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;

namespace Linkuity.TestSupport;

/// <summary>
/// Decorates a REAL <see cref="IIndexedCandidateRetrievalStrategy"/> and records, at the moment of
/// every index mutation, whether the durable write that mutation belongs to was already visible
/// OUTSIDE the store — as reported by the <c>durableWriteVisible</c> probe the test supplies.
/// <para>
/// This exists to pin the crash-consistency ordering invariant the metadata stores rely on: the
/// durable write (JSON save / SQL commit) MUST commit before its corresponding Lucene mutation. The
/// reverse order lets Lucene durably serve documents for records that never existed in the store if
/// the durable write then fails, with no self-healing path back (the drift check's live-count
/// comparison cannot see a net-zero change). Asserting on index state *after* the call returns —
/// what the existing correction/deletion tests do — cannot distinguish the two orderings, because
/// both end with the same index and store contents on the success path. Only observing the durable
/// store from inside the index mutation can.
/// </para>
/// <para>
/// The inner index is real, so every call still has its real effect and the test can go on to
/// assert real retrieval behavior. The probe is deliberately supplied by the caller: "the durable
/// write is visible" means reading the committed JSON file on the local backend and querying over a
/// SEPARATE connection (outside the store's still-open transaction) on the PostgreSQL backend, but
/// the ordering invariant being proven is identical on both.
/// </para>
/// </summary>
public sealed class DurableWriteOrderingSpyIndex(
    IIndexedCandidateRetrievalStrategy inner,
    Func<bool> durableWriteVisible) : IIndexedCandidateRetrievalStrategy
{
    private readonly List<string> _mutations = [];
    private readonly List<bool> _durableWriteVisibleAtMutation = [];

    /// <summary>Every index mutation observed, in call order. Reads (Retrieve) are not mutations.</summary>
    public IReadOnlyList<string> Mutations => _mutations;

    /// <summary>
    /// Parallel to <see cref="Mutations"/>: what the probe reported at the instant of each mutation.
    /// All-true means every index mutation ran after the durable write became visible.
    /// </summary>
    public IReadOnlyList<bool> DurableWriteVisibleAtMutation => _durableWriteVisibleAtMutation;

    public string Name => inner.Name;

    public long Count => inner.Count;

    // A read, not a mutation — deliberately not recorded. Retrieve runs mid-resolve (before the
    // durable write) by design on the ingest paths; only mutations carry the ordering invariant.
    public IReadOnlyList<EntityRecord> Retrieve(
        EntityRecord record, IReadOnlyCollection<EntityRecord> corpus, MatchingProfile profile)
        => inner.Retrieve(record, corpus, profile);

    public void Index(EntityRecord record)
    {
        Observe($"Index({record.Id})");
        inner.Index(record);
    }

    public void Update(EntityRecord record)
    {
        Observe($"Update({record.Id})");
        inner.Update(record);
    }

    public void Remove(Guid recordId)
    {
        Observe($"Remove({recordId})");
        inner.Remove(recordId);
    }

    public void Rebuild(IEnumerable<EntityRecord> records)
    {
        // Materialized once before observing: the sequence may be lazy, and enumerating it here and
        // again in the inner index would run the underlying query twice.
        var materialized = records as IReadOnlyCollection<EntityRecord> ?? records.ToList();
        Observe($"Rebuild({materialized.Count})");
        inner.Rebuild(materialized);
    }

    public void Commit()
    {
        Observe("Commit");
        inner.Commit();
    }

    public void Dispose() => inner.Dispose();

    private void Observe(string mutation)
    {
        _mutations.Add(mutation);
        _durableWriteVisibleAtMutation.Add(durableWriteVisible());
    }
}
