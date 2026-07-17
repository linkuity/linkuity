using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies;

public interface ICandidateRetrievalStrategy
{
    string Name { get; }

    /// <summary>
    /// Returns the candidate records to score against. The default linear
    /// implementation returns the whole corpus (the current scan); Milestone 15
    /// swaps in a Lucene-backed implementation behind this same interface.
    /// </summary>
    IReadOnlyList<EntityRecord> Retrieve(EntityRecord record, IReadOnlyCollection<EntityRecord> corpus, MatchingProfile profile);
}
