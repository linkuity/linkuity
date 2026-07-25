using Linkuity.Core.Models;
using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

public class NGramBlockingTests
{
    private static readonly IBlockingStrategy Strategy = new NGramBlockingStrategy(3);

    [Fact]
    public void NGram_EmitsTrigramsOfEachToken()
    {
        var record = TestRecords.Person("r", new Dictionary<string, string> { ["last_name"] = "Smith" });

        var keys = Strategy.GenerateKeys(record, TestProfiles.Person);

        // "smith" is a single token -> smi, mit, ith
        Assert.Contains("ngram:smi", keys);
        Assert.Contains("ngram:mit", keys);
        Assert.Contains("ngram:ith", keys);
        Assert.Equal(3, keys.Count);
    }

    [Fact]
    public void NGram_VariantsShareSomeGrams()
    {
        var smith = Strategy.GenerateKeys(TestRecords.Person("a", new Dictionary<string, string> { ["last_name"] = "Smith" }), TestProfiles.Person);
        var smyth = Strategy.GenerateKeys(TestRecords.Person("b", new Dictionary<string, string> { ["last_name"] = "Smithe" }), TestProfiles.Person);

        Assert.NotEmpty(smith.Intersect(smyth));
    }

    [Fact]
    public void NGram_UsesWholeTokenWhenShorterThanN()
    {
        var record = TestRecords.Person("r", new Dictionary<string, string> { ["last_name"] = "Ng" });

        Assert.Equal(["ngram:ng"], Strategy.GenerateKeys(record, TestProfiles.Person));
    }

    [Fact]
    public void NGram_IgnoresIdentifierFields()
    {
        var record = TestRecords.Person("r", new Dictionary<string, string> { ["email"] = "alice@example.com" });

        Assert.Empty(Strategy.GenerateKeys(record, TestProfiles.Person));
    }

    [Fact]
    public void NGram_NeverSpansWordBoundaries()
    {
        // SERVICE|INC concatenated would yield the junk gram "ein" (SERVIC-E,IN-C), which
        // falsely collides with BOEING's genuine "ein". Per-token gramming must not emit it.
        var keys = Strategy.GenerateKeys(
            TestRecords.Person("r", new Dictionary<string, string> { ["organization_name"] = "UNITED PARCEL SERVICE INC" }),
            TestProfiles.Person);

        Assert.DoesNotContain("ngram:ein", keys);
        Assert.Contains("ngram:ser", keys); // within-word grams still present
        Assert.Contains("ngram:inc", keys); // short token emits itself
    }
}
