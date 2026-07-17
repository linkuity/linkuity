using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Linear scan gated by blocking keys: a corpus record is a candidate only if it
/// shares at least one blocking key (OrdinalIgnoreCase) with the incoming record.
/// This reproduces the gate the durable path's old Score() provided (0 without a
/// shared key) so the weighted scorer is never asked to score unrelated pairs. The
/// Lucene strategy supersedes this for scale; this is the engine's no-index default.
/// </summary>
public sealed class BlockingAwareLinearRetrievalStrategy : ICandidateRetrievalStrategy
{
    public string Name => "blocking-linear";

    public IReadOnlyList<EntityRecord> Retrieve(EntityRecord record, IReadOnlyCollection<EntityRecord> corpus, MatchingProfile profile)
    {
        if (record.BlockingKeys.Count == 0)
            return [];

        var keys = record.BlockingKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return corpus
            .Where(candidate => candidate.BlockingKeys.Any(keys.Contains))
            .ToList();
    }
}
