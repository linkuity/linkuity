using Linkuity.Core.Models;
using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

public class NGramBlockingTests
{
    [Fact]
    public void NGram_EmitsTrigramsOfNormalizedValue()
    {
        IBlockingStrategy strategy = new NGramBlockingStrategy(3);
        var record = TestRecords.Person("r", new Dictionary<string, string> { ["last_name"] = "Smith" });

        var keys = strategy.GenerateKeys(record, TestProfiles.Person);

        // "smith" -> smi, mit, ith
        Assert.Contains("ngram:smi", keys);
        Assert.Contains("ngram:mit", keys);
        Assert.Contains("ngram:ith", keys);
        Assert.Equal(3, keys.Count);
    }

    [Fact]
    public void NGram_VariantsShareSomeGrams()
    {
        IBlockingStrategy strategy = new NGramBlockingStrategy(3);
        var smith = strategy.GenerateKeys(TestRecords.Person("a", new Dictionary<string, string> { ["last_name"] = "Smith" }), TestProfiles.Person);
        var smyth = strategy.GenerateKeys(TestRecords.Person("b", new Dictionary<string, string> { ["last_name"] = "Smithe" }), TestProfiles.Person);

        Assert.NotEmpty(smith.Intersect(smyth));
    }

    [Fact]
    public void NGram_UsesWholeValueWhenShorterThanN()
    {
        IBlockingStrategy strategy = new NGramBlockingStrategy(3);
        var record = TestRecords.Person("r", new Dictionary<string, string> { ["last_name"] = "Ng" });

        Assert.Equal(["ngram:ng"], strategy.GenerateKeys(record, TestProfiles.Person));
    }

    [Fact]
    public void NGram_IgnoresIdentifierFields()
    {
        IBlockingStrategy strategy = new NGramBlockingStrategy(3);
        var record = TestRecords.Person("r", new Dictionary<string, string> { ["email"] = "alice@example.com" });

        Assert.Empty(strategy.GenerateKeys(record, TestProfiles.Person));
    }
}
