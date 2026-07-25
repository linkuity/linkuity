using System.Runtime.CompilerServices;
using Linkuity.Core.Models;
using Linkuity.Matching.Blocking;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Linear scan gated by blocking keys: a corpus record is a candidate only if it
/// shares at least one blocking key (OrdinalIgnoreCase) with the incoming record.
/// This reproduces the gate the durable path's old Score() provided (0 without a
/// shared key) so the weighted scorer is never asked to score unrelated pairs. The
/// Lucene strategy supersedes this for scale; this is the engine's no-index default.
/// When the profile sets MaxBlockSize, keys whose corpus block exceeds it are
/// suppressed from candidacy (BlockingKeySuppressionPolicy); unset means no
/// suppression — completeness is this path's contract.
/// </summary>
public sealed class BlockingAwareLinearRetrievalStrategy : ICandidateRetrievalStrategy
{
    // Corpus key frequencies, weak-keyed on the corpus instance: batch matching calls
    // Retrieve once per record over the same corpus collection, so the counting pass
    // runs once per batch and is reclaimed with the corpus.
    private static readonly ConditionalWeakTable<IReadOnlyCollection<EntityRecord>, Dictionary<string, int>> FrequencyCache = new();

    public string Name => "blocking-linear";

    public IReadOnlyList<EntityRecord> Retrieve(EntityRecord record, IReadOnlyCollection<EntityRecord> corpus, MatchingProfile profile)
    {
        if (record.BlockingKeys.Count == 0)
            return [];

        HashSet<string> keys;
        if (profile.MaxBlockSize is { } maxBlockSize)
        {
            var policy = new BlockingKeySuppressionPolicy(maxBlockSize);
            var frequencies = FrequencyCache.GetValue(corpus, CountKeyFrequencies);
            keys = policy
                .ActiveKeys(record, key => frequencies.GetValueOrDefault(key))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (keys.Count == 0)
                return [];
        }
        else
        {
            keys = record.BlockingKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return corpus
            .Where(candidate => candidate.BlockingKeys.Any(keys.Contains))
            .ToList();
    }

    private static Dictionary<string, int> CountKeyFrequencies(IReadOnlyCollection<EntityRecord> corpus)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in corpus)
            foreach (var key in record.BlockingKeys.Distinct(StringComparer.OrdinalIgnoreCase))
                counts[key] = counts.GetValueOrDefault(key) + 1;
        return counts;
    }
}
