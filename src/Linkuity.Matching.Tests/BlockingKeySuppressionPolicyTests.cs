using Linkuity.Core.Models;
using Linkuity.Matching.Blocking;

namespace Linkuity.Matching.Tests;

public class BlockingKeySuppressionPolicyTests
{
    private static EntityRecord Record(params string[] keys) => new()
    {
        Id = Guid.NewGuid(), ProjectId = Guid.Empty, SourceId = Guid.Empty, IngestBatchId = Guid.Empty,
        SourceRecordId = "r", Fields = new Dictionary<string, string>(), BlockingKeys = keys,
        CreatedAt = DateTimeOffset.UnixEpoch
    };

    [Fact]
    public void AtThreshold_IsActive_AboveThreshold_IsSuppressed()
    {
        var policy = new BlockingKeySuppressionPolicy(50);
        Assert.False(policy.IsSuppressed("name:inc", 50)); // boundary: exactly at max stays active
        Assert.True(policy.IsSuppressed("name:inc", 51));
    }

    [Fact]
    public void ActiveKeys_FiltersOnlySuppressedKeys()
    {
        var policy = new BlockingKeySuppressionPolicy(2);
        var frequencies = new Dictionary<string, int> { ["fp:acme"] = 2, ["name:inc"] = 56 };

        var active = policy.ActiveKeys(Record("fp:acme", "name:inc"), k => frequencies[k]);

        Assert.Equal(["fp:acme"], active);
    }

    [Fact]
    public void ActiveKeys_AllSuppressed_ReturnsEmpty()
    {
        var policy = new BlockingKeySuppressionPolicy(1);
        var active = policy.ActiveKeys(Record("name:inc", "prefix:inte"), _ => 100);
        Assert.Empty(active);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ctor_RejectsThresholdBelowOne(int max)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new BlockingKeySuppressionPolicy(max));
}
