using Linkuity.Core.Models;
using Linkuity.Matching;

namespace Linkuity.Matching.Tests;

public class MatchingEngineBlockingKeyTests
{
    [Fact]
    public void GenerateBlockingKeys_ProducesExactAndTokenKeysForAPersonRecord()
    {
        var engine = MatchingDefaults.CreateEngine();
        var profile = MatchingDefaults.CreatePersonProfile();
        var record = new EntityRecord
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            SourceId = Guid.NewGuid(),
            IngestBatchId = Guid.NewGuid(),
            SourceRecordId = "r1",
            Fields = new Dictionary<string, string> { ["email"] = "alice@example.com", ["last_name"] = "Smith" },
            CreatedAt = DateTimeOffset.UtcNow
        };

        var keys = engine.GenerateBlockingKeys(record, profile);

        Assert.Contains("email:aliceexamplecom", keys);
        Assert.Contains("name:smith", keys);
        // sorted, deduped
        Assert.Equal(keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase), keys);
        Assert.Equal(keys.Distinct(StringComparer.OrdinalIgnoreCase).Count(), keys.Count);
    }

    [Fact]
    public void GenerateBlockingKeys_RecomputesEvenWhenRecordAlreadyHasKeys()
    {
        var engine = MatchingDefaults.CreateEngine();
        var profile = MatchingDefaults.CreatePersonProfile();
        var record = new EntityRecord
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            SourceId = Guid.NewGuid(),
            IngestBatchId = Guid.NewGuid(),
            SourceRecordId = "r1",
            Fields = new Dictionary<string, string> { ["email"] = "alice@example.com" },
            BlockingKeys = ["stale:key"],
            CreatedAt = DateTimeOffset.UtcNow
        };

        var keys = engine.GenerateBlockingKeys(record, profile);

        Assert.Contains("email:aliceexamplecom", keys);
        Assert.DoesNotContain("stale:key", keys);
    }
}
