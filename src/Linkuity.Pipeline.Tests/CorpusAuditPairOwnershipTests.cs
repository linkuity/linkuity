namespace Linkuity.Pipeline.Tests;

public class CorpusAuditPairOwnershipTests
{
    private static List<(int, int)> Collect(KeyIndex index, int? maxBlockSize, out long occurrences)
    {
        var seen = new List<(int, int)>();
        occurrences = CorpusAuditService.ForEachCandidatePair(
            index, maxBlockSize, (l, r) => seen.Add((l, r)), CancellationToken.None);
        return seen;
    }

    /// <summary>Records 0 and 1 share FOUR keys. Ownership by lowest shared active key must
    /// emit the pair once; without the rule it would appear four times.</summary>
    [Fact]
    public void PairSharingManyKeysIsEmittedExactlyOnce()
    {
        var index = CorpusAuditFixtures.SyntheticIndex(
            ["k1", "k2", "k3", "k4"],
            ["k1", "k2", "k3", "k4"]);

        var seen = Collect(index, null, out var occurrences);

        Assert.Single(seen);
        Assert.Equal((0, 1), seen[0]);
        Assert.Equal(4, occurrences);   // visited four times, emitted once
    }

    [Fact]
    public void EmitsLeftIndexLowerThanRight()
    {
        var index = CorpusAuditFixtures.SyntheticIndex(["k1"], ["k1"], ["k1"]);

        var seen = Collect(index, null, out _);

        Assert.Equal(3, seen.Count);
        Assert.All(seen, p => Assert.True(p.Item1 < p.Item2));
    }

    /// <summary>A suppressed key owns no pairs. Records 0-3 share only the over-large key;
    /// records 4 and 5 also share a rare key and must survive.</summary>
    [Fact]
    public void SuppressedKeysOwnNoPairs()
    {
        var index = CorpusAuditFixtures.SyntheticIndex(
            ["common"], ["common"], ["common"], ["common"], ["common", "rare"], ["common", "rare"]);

        // "common" has 6 members -> corpus frequency 5 > 4 -> suppressed. "rare" has 2 -> active.
        var seen = Collect(index, maxBlockSize: 4, out _);

        Assert.Single(seen);
        Assert.Equal((4, 5), seen[0]);
    }

    /// <summary>Engine parity (BlockingAuditService.cs:118): a block of exactly maxBlockSize+1
    /// has corpus frequency maxBlockSize and stays ACTIVE. Three members, max 2 -> active.</summary>
    [Fact]
    public void BlockOfExactlyMaxPlusOneStaysActive()
    {
        var index = CorpusAuditFixtures.SyntheticIndex(["k"], ["k"], ["k"]);

        var seen = Collect(index, maxBlockSize: 2, out _);

        Assert.Equal(3, seen.Count);
    }

    [Fact]
    public void BlockOfMaxPlusTwoIsSuppressed()
    {
        var index = CorpusAuditFixtures.SyntheticIndex(["k"], ["k"], ["k"], ["k"]);

        var seen = Collect(index, maxBlockSize: 2, out _);

        Assert.Empty(seen);
    }

    /// <summary>Ownership must fall through to the next active key when the lowest shared key
    /// is suppressed — otherwise a pair whose cheapest key is over-large is silently dropped.</summary>
    [Fact]
    public void OwnershipFallsThroughSuppressedLowestKey()
    {
        // "aaa" interns to id 0 BECAUSE it is listed first in record 0 below (SyntheticIndex
        // interns in first-seen order, not alphabetically) — reordering this fixture to
        // ["zzz", "aaa"] would flip the ids and quietly invalidate what this test proves.
        // "aaa" is shared by all four records -> suppressed at max 2.
        // "zzz" interns second (id 1) and is shared only by records 0 and 1 -> must own the pair.
        var index = CorpusAuditFixtures.SyntheticIndex(
            ["aaa", "zzz"], ["aaa", "zzz"], ["aaa"], ["aaa"]);

        var seen = Collect(index, maxBlockSize: 2, out _);

        Assert.Single(seen);
        Assert.Equal((0, 1), seen[0]);
    }

    /// <summary>Ownership must survive when the two records' key sets are NON-IDENTICAL, forcing
    /// the two-pointer scan in LowestSharedActiveKey to advance past a mismatched key before it
    /// finds the shared owner. Every other test above this one in the file gives both records
    /// identical key arrays, so only the equal branch ever executes there. This case exercises
    /// ONLY the `left[i] &lt; right[j]` branch (`i++`) — the symmetric `left[i] &gt; right[j]`
    /// branch (`j++`) is NOT covered here; see OwnershipSurvivesLowerKeyOnTheRightSide for that.
    /// Ids: "a" interns first (0), "x" second (1), "b" third (2) -> record 0 = [0,1], record 1 =
    /// [1,2]. The scan must step past "a" vs "b" (left[i] &lt; right[j], so i++) before landing on
    /// the shared "x".</summary>
    [Fact]
    public void OwnershipSurvivesNonIdenticalKeySets()
    {
        var index = CorpusAuditFixtures.SyntheticIndex(["a", "x"], ["b", "x"]);

        var seen = Collect(index, null, out _);

        Assert.Single(seen);
        Assert.Equal((0, 1), seen[0]);
    }

    /// <summary>Forces the OTHER mismatch branch in LowestSharedActiveKey: `left[i] &gt; right[j]`
    /// (`j++`). SyntheticIndex interns in first-seen order, so a later record can normally only
    /// introduce HIGHER ids; this branch only fires when a later record shares a LOWER-id key that
    /// an earlier record lacks. "b" interns to id 0 (first seen in record 0), "a" to id 1 (first
    /// seen in record 1) — first-seen order, NOT alphabetical. Record 2 carries both, so
    /// RecordKeys[2] = [0,1]. For pair (1,2): RecordKeys[1] = [1], RecordKeys[2] = [0,1]; the scan
    /// starts at left[0]=1 vs right[0]=0, so left[i] &gt; right[j] and ONLY the j++ branch can
    /// advance to the shared key 1. Mutating that j++ to i++ walks left off its end, returns -1,
    /// and drops the pair.</summary>
    [Fact]
    public void OwnershipSurvivesLowerKeyOnTheRightSide()
    {
        var index = CorpusAuditFixtures.SyntheticIndex(["b"], ["a"], ["b", "a"]);

        var seen = Collect(index, null, out _);

        Assert.Equal(2, seen.Count);
        Assert.Contains((0, 2), seen);
        Assert.Contains((1, 2), seen);
    }

    /// <summary>Ownership must fall through TWO consecutive suppressed shared keys, not just one,
    /// before reaching the active owner. "aaa" and "bbb" are both shared by all five records and
    /// suppressed at max 2; "zzz" is shared only by records 0 and 1 and is active. This exercises
    /// repeated (suppressed-equal -> i++, j++) steps in LowestSharedActiveKey, which a single-skip
    /// fixture cannot distinguish from a fixture that only ever skips once.</summary>
    [Fact]
    public void OwnershipFallsThroughMultipleConsecutiveSuppressedKeys()
    {
        var index = CorpusAuditFixtures.SyntheticIndex(
            ["aaa", "bbb", "zzz"], ["aaa", "bbb", "zzz"],
            ["aaa", "bbb"], ["aaa", "bbb"], ["aaa", "bbb"]);

        var seen = Collect(index, maxBlockSize: 2, out _);

        Assert.Single(seen);
        Assert.Equal((0, 1), seen[0]);
    }
}
