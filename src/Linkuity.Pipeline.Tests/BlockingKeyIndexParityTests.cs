using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Xunit;

namespace Linkuity.Pipeline.Tests;

/// <summary>
/// Pins the extracted primitives against the behaviour they had inside CorpusAuditService.
/// Written BEFORE the extraction so it fails if the move changes anything: the extraction's
/// only defensible outcome is identical numbers.
/// </summary>
public class BlockingKeyIndexParityTests
{
    private static (IReadOnlyList<EntityRecord> Records, MatchingProfile Profile) Fixture()
    {
        var profile = CorpusAuditFixtures.Profile();
        var records = new List<EntityRecord>
        {
            CorpusAuditFixtures.Org("r1", "ACME TRADING LIMITED"),
            CorpusAuditFixtures.Org("r2", "ACME TRADING LTD"),
            CorpusAuditFixtures.Org("r3", "ACME TRADING LIMITED"),
            CorpusAuditFixtures.Org("r4", "BOREAL HOLDINGS SA"),
            CorpusAuditFixtures.Org("r5", "ZENITH PLC"),
        };
        return (records, profile);
    }

    [Fact]
    public void BuildProducesAscendingRecordKeyRows()
    {
        var (records, profile) = Fixture();
        var index = BlockingKeyIndex.Build(records, profile, MatchingDefaults.CreateRegistry());

        Assert.Equal(records.Count, index.RecordKeys.Length);
        foreach (var row in index.RecordKeys)
            for (var i = 1; i < row.Length; i++)
                Assert.True(row[i - 1] < row[i], "RecordKeys rows must be strictly ascending");
    }

    [Fact]
    public void KeyCountMatchesMemberCardinality()
    {
        var (records, profile) = Fixture();
        var index = BlockingKeyIndex.Build(records, profile, MatchingDefaults.CreateRegistry());

        for (var k = 0; k < index.KeyCount.Length; k++)
            Assert.Equal(index.KeyMembers[k].Length, index.KeyCount[k]);
    }

    [Fact]
    public void SuppressionUsesPerQueryFrequencySoABlockOfExactlyMaxPlusOneStaysActive()
    {
        // Engine parity: a block of size S has per-query frequency S-1, suppressed iff S-1 > max.
        var (records, profile) = Fixture();
        var index = BlockingKeyIndex.Build(records, profile, MatchingDefaults.CreateRegistry());

        var suppressed = BlockingKeyIndex.SuppressedKeys(index, maxBlockSize: 2);
        for (var k = 0; k < index.KeyCount.Length; k++)
            Assert.Equal(index.KeyCount[k] - 1 > 2, suppressed[k]);
    }

    [Fact]
    public void EachCandidatePairIsEmittedExactlyOnce()
    {
        var (records, profile) = Fixture();
        var index = BlockingKeyIndex.Build(records, profile, MatchingDefaults.CreateRegistry());

        var seen = new List<(int, int)>();
        BlockingKeyIndex.ForEachCandidatePair(index, maxBlockSize: null, (a, b) => seen.Add((a, b)));

        Assert.Equal(seen.Count, seen.Distinct().Count());
        foreach (var (a, b) in seen) Assert.True(a < b, "pairs must be emitted low-first");
    }

    [Fact]
    public void SharesAnyActiveKeyAgreesWithSharedActiveKeys()
    {
        var (records, profile) = Fixture();
        var index = BlockingKeyIndex.Build(records, profile, MatchingDefaults.CreateRegistry());
        var suppressed = BlockingKeyIndex.SuppressedKeys(index, maxBlockSize: null);

        for (var a = 0; a < records.Count; a++)
            for (var b = a + 1; b < records.Count; b++)
            {
                var shared = BlockingKeyIndex.SharedActiveKeys(
                    index.RecordKeys[a], index.RecordKeys[b], suppressed);
                var any = BlockingKeyIndex.SharesAnyActiveKey(
                    index.RecordKeys[a], index.RecordKeys[b], suppressed);
                Assert.Equal(shared.Count > 0, any);
            }
    }
}
