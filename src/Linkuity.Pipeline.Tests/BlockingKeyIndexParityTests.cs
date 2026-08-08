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

    // A wider fixture than Fixture(): repeated names (block sizes > 1), suffix-stripped names,
    // single-token names, and a name whose only distinguishing token is numeric -- so the
    // agreement assertion below is exercised across every shape the three configured strategies
    // (fingerprint/token/acronym) key differently, not just the five it was written against.
    private static (IReadOnlyList<EntityRecord> Records, MatchingProfile Profile) WideFixture()
    {
        var profile = CorpusAuditFixtures.Profile();
        var records = new List<EntityRecord>
        {
            CorpusAuditFixtures.Org("w1", "ACME TRADING LIMITED"),
            CorpusAuditFixtures.Org("w2", "ACME TRADING LTD"),
            CorpusAuditFixtures.Org("w3", "acme trading limited"),   // case-only variant
            CorpusAuditFixtures.Org("w4", "THE BOREAL HOLDINGS SA"), // leading article dropped
            CorpusAuditFixtures.Org("w5", "ZENITH"),                 // single token, no suffix
            CorpusAuditFixtures.Org("w6", "AT & T CORP"),            // ampersand collapse
            CorpusAuditFixtures.Org("w7", "WAL-MART STORES INC"),    // hyphen variant
            CorpusAuditFixtures.Org("w8", "PROJECT 2000 GMBH"),      // numeric token
        };
        return (records, profile);
    }

    /// <summary>
    /// BlockingAuditService.Audit derives keys TWICE: a legacy string-keyed pass builds
    /// PerRecord/Blocks/reachability, and BlockingKeyIndex.Build feeds the candidate-pair counter.
    /// Nothing asserted the two agreed -- and a second implementation of key generation silently
    /// diverging from the first is precisely what extracting BlockingKeyIndex was meant to end.
    /// (CandidatePairCountMatchesTheOwnershipWalk does not cover this: it compares the walk to
    /// itself.) Compared case-INSENSITIVELY because both derivations intern keys under
    /// StringComparer.OrdinalIgnoreCase, so which casing survives is an interning artifact
    /// (per-record for the legacy HashSet, corpus-global for the index) and not a difference in
    /// the key SET, which is what candidacy depends on.
    /// </summary>
    [Fact]
    public void LegacyPerRecordKeysAgreeWithTheInternedIndexPerRecord()
    {
        foreach (var (records, profile) in new[] { Fixture(), WideFixture() })
        {
            var result = new BlockingAuditService(MatchingDefaults.CreateRegistry()).Audit(records, profile);
            var index = BlockingKeyIndex.Build(records, profile, MatchingDefaults.CreateRegistry());

            Assert.Equal(records.Count, result.PerRecord.Count);
            Assert.Equal(records.Count, index.RecordKeys.Length);

            for (var i = 0; i < records.Count; i++)
            {
                Assert.Equal(records[i].SourceRecordId, result.PerRecord[i].SourceRecordId);

                var fromIndex = index.RecordKeys[i]
                    .Select(id => index.KeyNames[id])
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var fromLegacy = result.PerRecord[i].AllKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);

                Assert.True(
                    fromIndex.SetEquals(fromLegacy),
                    $"record {records[i].SourceRecordId}: index-only " +
                    $"[{string.Join(",", fromIndex.Except(fromLegacy, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))}], " +
                    $"legacy-only [{string.Join(",", fromLegacy.Except(fromIndex, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))}]");

                // AllKeys must also be exactly the union of the per-strategy lists: if it were not,
                // the set above could agree while the per-strategy attribution disagreed.
                var unionOfStrategies = result.PerRecord[i].KeysByStrategy
                    .SelectMany(kv => kv.Value)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                Assert.True(unionOfStrategies.SetEquals(fromLegacy));
            }

            // And the block sizes the two derivations imply must be identical key-for-key --
            // block size is what suppression is decided from, so a divergence here would change
            // which pairs the audit calls reachable.
            var legacySizes = result.Blocks.ToDictionary(b => b.Key, b => b.Size, StringComparer.OrdinalIgnoreCase);
            var indexSizes = Enumerable.Range(0, index.KeyNames.Length)
                .ToDictionary(k => index.KeyNames[k], k => index.KeyCount[k], StringComparer.OrdinalIgnoreCase);

            // Compared by LOOKUP, not by zipping two ordered lists: the key strings themselves may
            // differ in casing between the derivations (interning artifact), and a list comparison
            // would fail on that cosmetic difference rather than on a real size divergence.
            Assert.Equal(legacySizes.Count, indexSizes.Count);
            foreach (var (key, size) in legacySizes)
            {
                Assert.True(indexSizes.TryGetValue(key, out var indexSize), $"key '{key}' missing from the interned index");
                Assert.Equal(size, indexSize);
            }
        }
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
