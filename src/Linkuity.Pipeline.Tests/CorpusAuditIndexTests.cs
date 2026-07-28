using Linkuity.Core.Models;
using Linkuity.Matching;

namespace Linkuity.Pipeline.Tests;

public class CorpusAuditIndexTests
{
    private static readonly EntityRecord[] Records =
    [
        CorpusAuditFixtures.Org("a", "ACME WIDGETS INC"),
        CorpusAuditFixtures.Org("b", "ACME WIDGETS"),
        CorpusAuditFixtures.Org("c", "ZETA HOLDINGS LLC")
    ];

    /// <summary>
    /// The index must produce exactly the keys BlockingAuditService produces. That service is
    /// the shipped definition of "the profile's keys"; divergence means the corpus audit is
    /// measuring a different engine. This is the fidelity anchor for the whole instrument.
    /// </summary>
    [Fact]
    public void KeysMatchBlockingAuditServiceExactly()
    {
        var profile = CorpusAuditFixtures.Profile();
        var expected = new BlockingAuditService(MatchingDefaults.CreateRegistry())
            .Audit(Records, profile)
            .PerRecord.ToDictionary(
                r => r.SourceRecordId,
                r => r.AllKeys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.Ordinal);

        var index = CorpusAuditService.BuildIndex(Records, profile, MatchingDefaults.CreateRegistry());

        Assert.Equal(Records.Length, expected.Count);
        for (var i = 0; i < Records.Length; i++)
        {
            var actual = index.RecordKeys[i]
                .Select(k => index.KeyNames[k])
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();
            Assert.Equal(expected[Records[i].SourceRecordId], actual);
        }
    }

    [Fact]
    public void RecordKeyIdsAreStrictlyAscending()
    {
        var index = CorpusAuditService.BuildIndex(
            Records, CorpusAuditFixtures.Profile(), MatchingDefaults.CreateRegistry());

        foreach (var keys in index.RecordKeys)
            for (var i = 1; i < keys.Length; i++)
                Assert.True(keys[i] > keys[i - 1], "record key ids must be strictly ascending");
    }

    [Fact]
    public void KeyMembersAndKeyCountAgree()
    {
        var index = CorpusAuditService.BuildIndex(
            Records, CorpusAuditFixtures.Profile(), MatchingDefaults.CreateRegistry());

        for (var k = 0; k < index.KeyCount.Length; k++)
            Assert.Equal(index.KeyCount[k], index.KeyMembers[k].Length);
    }
}
