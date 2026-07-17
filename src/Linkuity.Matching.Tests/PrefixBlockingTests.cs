using Linkuity.Core.Models;
using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

public class PrefixBlockingTests
{
    [Fact]
    public void Prefix_EmitsFirstNCharsOfNormalizedNameFields()
    {
        IBlockingStrategy strategy = new PrefixBlockingStrategy(4);
        var record = TestRecords.Person("r", new Dictionary<string, string> { ["last_name"] = "AndERson" });

        var keys = strategy.GenerateKeys(record, TestProfiles.Person);

        Assert.Contains("prefix:ande", keys);
    }

    [Fact]
    public void Prefix_UsesWholeValueWhenShorterThanLength()
    {
        IBlockingStrategy strategy = new PrefixBlockingStrategy(4);
        var record = TestRecords.Person("r", new Dictionary<string, string> { ["last_name"] = "Ng" });

        Assert.Contains("prefix:ng", strategy.GenerateKeys(record, TestProfiles.Person));
    }

    [Fact]
    public void Prefix_IgnoresNonBlockingAndNonNameFields()
    {
        IBlockingStrategy strategy = new PrefixBlockingStrategy(4);
        // email is an identifier type, not a name/text type -> no prefix key.
        var record = TestRecords.Person("r", new Dictionary<string, string> { ["email"] = "alice@example.com" });

        Assert.Empty(strategy.GenerateKeys(record, TestProfiles.Person));
    }

    [Fact]
    public void Prefix_ReturnsDistinctKeys()
    {
        IBlockingStrategy strategy = new PrefixBlockingStrategy(4);
        var record = TestRecords.Person("r", new Dictionary<string, string> { ["first_name"] = "Anderson", ["last_name"] = "Anderson" });

        var keys = strategy.GenerateKeys(record, TestProfiles.Person);

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }
}
