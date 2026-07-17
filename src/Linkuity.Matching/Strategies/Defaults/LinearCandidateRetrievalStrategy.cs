using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Wraps the current linear scan: every existing record is a candidate. Scoring
/// then assigns 0 to records with no shared blocking key, matching the durable path.
/// Milestone 15 replaces this with Lucene Top-N retrieval behind the same interface.
/// </summary>
public sealed class LinearCandidateRetrievalStrategy : ICandidateRetrievalStrategy
{
    public string Name => "linear";

    public IReadOnlyList<EntityRecord> Retrieve(EntityRecord record, IReadOnlyCollection<EntityRecord> corpus, MatchingProfile profile)
        => corpus as IReadOnlyList<EntityRecord> ?? corpus.ToList();
}
