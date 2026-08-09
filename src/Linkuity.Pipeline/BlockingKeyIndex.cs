using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;

namespace Linkuity.Pipeline;

/// <summary>Interned blocking-key index. RecordKeys rows are ascending — Task 4's
/// lowest-shared-key ownership rule needs it for a linear intersection scan.</summary>
internal sealed record KeyIndex(int[][] RecordKeys, int[] KeyCount, int[][] KeyMembers, string[] KeyNames);

/// <summary>
/// Blocking-key primitives shared by CorpusAuditService and BlockingAuditService. Extracted so
/// there is exactly ONE implementation of key generation, suppression and candidate-pair
/// enumeration: a second implementation of these semantics is how audit numbers silently diverge
/// from engine behaviour.
/// </summary>
internal static class BlockingKeyIndex
{
    private static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;

    internal static KeyIndex Build(
        IReadOnlyList<EntityRecord> records, MatchingProfile profile, IStrategyRegistry registry,
        CancellationToken ct = default)
    {
        var normalization = registry.Normalization[profile.NormalizationStrategy];
        var keyIds = new Dictionary<string, int>(KeyComparer);
        var keyNames = new List<string>();
        var members = new List<List<int>>();
        var recordKeys = new int[records.Count][];

        for (var i = 0; i < records.Count; i++)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();

            var normalized = normalization.Normalize(records[i], profile);
            var ids = new SortedSet<int>();
            foreach (var strategyName in profile.BlockingStrategies)
                foreach (var key in registry.Blocking[strategyName].GenerateKeys(normalized, profile))
                {
                    if (!keyIds.TryGetValue(key, out var id))
                    {
                        id = keyNames.Count;
                        keyIds[key] = id;
                        keyNames.Add(key);
                        members.Add([]);
                    }
                    ids.Add(id);
                }

            recordKeys[i] = [.. ids];
            foreach (var id in ids) members[id].Add(i);
        }

        var keyMembers = new int[members.Count][];
        var keyCount = new int[members.Count];
        for (var k = 0; k < members.Count; k++)
        {
            keyMembers[k] = [.. members[k]];
            keyCount[k] = members[k].Count;
        }
        return new KeyIndex(recordKeys, keyCount, keyMembers, [.. keyNames]);
    }

    /// <summary>Engine parity: blocking-linear counts key frequency over a corpus that EXCLUDES
    /// the query record, so a block of size S has per-query frequency S-1 and is suppressed iff
    /// S-1 > maxBlockSize (BlockingAuditService.cs:118).</summary>
    internal static bool[] SuppressedKeys(KeyIndex index, int? maxBlockSize)
    {
        var suppressed = new bool[index.KeyCount.Length];
        if (maxBlockSize is not { } max) return suppressed;
        for (var k = 0; k < index.KeyCount.Length; k++)
            suppressed[k] = index.KeyCount[k] - 1 > max;
        return suppressed;
    }

    /// <summary>
    /// The EXACT block-pair visit count <see cref="ForEachCandidatePair"/> is about to make — the
    /// same per-key C(n,2) sum that walk performs, computed here up front purely so the merge
    /// policy's <c>comparisons</c> list (Audit) can be allocated at its final size in one shot.
    /// Emitted pairs are a subset of visits (dedup by lowest shared key), so this is always >= the
    /// list's eventual Count — never an under-estimate that would leave List&lt;T&gt; growing
    /// anyway. Without this, that list grows one doubling at a time up to ~11,000,007 entries on
    /// the SEC corpus, and because each intermediate array is well past the 85,000-byte Large
    /// Object Heap threshold, EVERY doubling is an LOH allocation — turning a single scoring walk
    /// into a repeated-full-GC-pause exercise. Read-only over index/suppressed; costs one pass over
    /// the key list, orders of magnitude cheaper than the walk it is sizing for.
    /// </summary>
    internal static int CandidatePairUpperBound(KeyIndex index, bool[] suppressed)
    {
        long total = 0;
        for (var k = 0; k < index.KeyCount.Length; k++)
        {
            if (suppressed[k]) continue;
            long n = index.KeyCount[k];
            total += n * (n - 1) / 2;
        }
        // Clamped, not overflow-checked: a capacity hint this large would itself be an
        // out-of-memory condition long before int.MaxValue is a realistic corpus size, and List<T>'s
        // constructor takes an int regardless.
        return total > int.MaxValue ? int.MaxValue : (int)total;
    }

    /// <summary>
    /// Emits each candidate pair EXACTLY ONCE with no deduplication structure: a pair is owned
    /// by the lowest active key id both records carry. Returns the OCCURRENCE count — how many
    /// block-pair visits happened — which is the real work measure and is always >= the number
    /// of emitted pairs.
    /// </summary>
    internal static long ForEachCandidatePair(
        KeyIndex index, int? maxBlockSize, Action<int, int> onPair, CancellationToken ct = default)
    {
        var suppressed = SuppressedKeys(index, maxBlockSize);
        long occurrences = 0;

        for (var k = 0; k < index.KeyMembers.Length; k++)
        {
            if (suppressed[k]) continue;
            var ids = index.KeyMembers[k];
            if (ids.Length < 2) continue;
            ct.ThrowIfCancellationRequested();

            for (var i = 0; i < ids.Length; i++)
                for (var j = i + 1; j < ids.Length; j++)
                {
                    occurrences++;
                    var a = ids[i];
                    var b = ids[j];
                    if (LowestSharedActiveKey(index.RecordKeys[a], index.RecordKeys[b], suppressed) == k)
                        onPair(a < b ? a : b, a < b ? b : a);
                }
        }
        return occurrences;
    }

    /// <summary>Lowest key id present in both ascending arrays and not suppressed; -1 if none.
    /// Internal (not private): FieldEvidenceCalibrationService recomputes the SAME pair's owning
    /// key — via the same suppressed array from <see cref="SuppressedKeys"/> — to attribute a
    /// candidate pair to the blocking field that produced it, and must call this exact scan rather
    /// than a second, potentially-divergent one.</summary>
    internal static int LowestSharedActiveKey(int[] left, int[] right, bool[] suppressed)
    {
        int i = 0, j = 0;
        while (i < left.Length && j < right.Length)
        {
            if (left[i] < right[j]) i++;
            else if (left[i] > right[j]) j++;
            else
            {
                if (!suppressed[left[i]]) return left[i];
                i++; j++;
            }
        }
        return -1;
    }

    /// <summary>Ascending-array intersection restricted to active keys. Allocation-free in the
    /// common case; the reachability classifier calls this once per true pair.</summary>
    internal static bool SharesAnyActiveKey(int[] a, int[] b, bool[] suppressed)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] < b[j]) i++;
            else if (a[i] > b[j]) j++;
            else { if (!suppressed[a[i]]) return true; i++; j++; }
        }
        return false;
    }

    /// <summary>Every active key both records carry, ascending. Used only for the capped
    /// diagnostic samples, never in a per-pair hot path over the whole corpus.</summary>
    internal static List<int> SharedActiveKeys(int[] a, int[] b, bool[] suppressed)
    {
        var shared = new List<int>();
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] < b[j]) i++;
            else if (a[i] > b[j]) j++;
            else { if (!suppressed[a[i]]) shared.Add(a[i]); i++; j++; }
        }
        return shared;
    }

    /// <summary>Keys both records carry REGARDLESS of suppression. The difference between this
    /// and SharedActiveKeys is exactly cause A: a pair that shares keys but shares no ACTIVE
    /// key was thrown away by the cap.</summary>
    internal static List<int> SharedKeysIgnoringSuppression(int[] a, int[] b)
    {
        var shared = new List<int>();
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] < b[j]) i++;
            else if (a[i] > b[j]) j++;
            else { shared.Add(a[i]); i++; j++; }
        }
        return shared;
    }
}
