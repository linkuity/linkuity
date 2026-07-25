using Linkuity.Core.Models;

namespace Linkuity.Matching.Blocking;

/// <summary>
/// The single definition of "this blocking key is too generic to drive candidate
/// generation": suppressed iff its block size exceeds the threshold (a block exactly at
/// the threshold stays active). Pure and corpus-blind — each seam supplies frequencies
/// from its natural source (in-memory counts, Lucene DocFreq, or the audit's inverted
/// index). Suppression is hard: a suppressed key contributes zero candidacy; a record
/// whose keys are all suppressed retrieves nothing (visible in the blocking audit).
/// </summary>
public sealed class BlockingKeySuppressionPolicy
{
    public BlockingKeySuppressionPolicy(int maxBlockSize)
    {
        if (maxBlockSize < 1)
            throw new ArgumentOutOfRangeException(nameof(maxBlockSize), "maxBlockSize must be at least 1.");
        MaxBlockSize = maxBlockSize;
    }

    public int MaxBlockSize { get; }

    public bool IsSuppressed(string key, int frequency) => frequency > MaxBlockSize;

    public IReadOnlyList<string> ActiveKeys(EntityRecord record, Func<string, int> frequencyOf)
        => record.BlockingKeys.Where(key => !IsSuppressed(key, frequencyOf(key))).ToList();
}
