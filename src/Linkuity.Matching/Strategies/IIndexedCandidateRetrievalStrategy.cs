using Linkuity.Core.Models;

namespace Linkuity.Matching.Strategies;

/// <summary>
/// A candidate-retrieval strategy backed by a durable, incrementally maintained
/// index (e.g. Lucene). Extends <see cref="ICandidateRetrievalStrategy"/> with the
/// index lifecycle the durable store drives: add, update, remove, and a full
/// rebuild from durable records (the index is a derived artifact). Lives in the
/// contracts project so the durable store (Milestone 16) depends only on this
/// abstraction, never on the Lucene adapter directly.
/// </summary>
public interface IIndexedCandidateRetrievalStrategy : ICandidateRetrievalStrategy, IDisposable
{
    /// <summary>The number of live documents currently in the index.</summary>
    long Count { get; }

    /// <summary>Adds a record to the index. Caller ensures the id is new.</summary>
    void Index(EntityRecord record);

    /// <summary>Replaces the indexed document for <paramref name="record"/>.Id (delete-by-id + add).</summary>
    void Update(EntityRecord record);

    /// <summary>Removes the document for the given record id, if present.</summary>
    void Remove(Guid recordId);

    /// <summary>Clears the index and rebuilds it from the given durable records.</summary>
    void Rebuild(IEnumerable<EntityRecord> records);

    /// <summary>Flushes pending changes to durable storage so they survive restart.</summary>
    void Commit();
}
