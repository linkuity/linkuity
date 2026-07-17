using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

public class BlockingAwareLinearRetrievalTests
{
    private static EntityRecord Record(string id, params string[] keys) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = Guid.NewGuid(),
        SourceId = Guid.NewGuid(),
        IngestBatchId = Guid.NewGuid(),
        SourceRecordId = id,
        Fields = new Dictionary<string, string>(),
        BlockingKeys = keys,
        CreatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public void Retrieve_ReturnsOnlyCorpusRecordsSharingABlockingKey()
    {
        var strategy = new BlockingAwareLinearRetrievalStrategy();
        var incoming = Record("in", "email:a", "name:smith");
        var shares = Record("shares", "name:smith");
        var disjoint = Record("disjoint", "name:jones");
        var profile = MatchingDefaults.CreatePersonProfile();

        var result = strategy.Retrieve(incoming, [shares, disjoint], profile);

        Assert.Contains(result, r => r.SourceRecordId == "shares");
        Assert.DoesNotContain(result, r => r.SourceRecordId == "disjoint");
    }

    [Fact]
    public void Retrieve_IsCaseInsensitiveOnKeys()
    {
        var strategy = new BlockingAwareLinearRetrievalStrategy();
        var incoming = Record("in", "EMAIL:A");
        var shares = Record("shares", "email:a");

        var result = strategy.Retrieve(incoming, [shares], MatchingDefaults.CreatePersonProfile());

        Assert.Single(result);
    }

    [Fact]
    public void Retrieve_ReturnsEmptyWhenIncomingHasNoKeys()
    {
        var strategy = new BlockingAwareLinearRetrievalStrategy();
        var incoming = Record("in");
        var other = Record("other", "name:smith");

        var result = strategy.Retrieve(incoming, [other], MatchingDefaults.CreatePersonProfile());

        Assert.Empty(result);
    }
}
