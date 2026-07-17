using Linkuity.Core.Models;
using Linkuity.Matching.Phonetics;
using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

public class PhoneticBlockingTests
{
    private static readonly IBlockingStrategy Strategy = new PhoneticBlockingStrategy();

    [Fact]
    public void Phonetic_LastNameVariants_ShareAKey()
    {
        var smith = Strategy.GenerateKeys(TestRecords.Person("a", new Dictionary<string, string> { ["last_name"] = "Smith" }), TestProfiles.Person);
        var smyth = Strategy.GenerateKeys(TestRecords.Person("b", new Dictionary<string, string> { ["last_name"] = "Smyth" }), TestProfiles.Person);

        Assert.NotEmpty(smith);
        Assert.NotEmpty(smith.Intersect(smyth));
    }

    [Fact]
    public void Phonetic_FullName_EncodesLastToken()
    {
        var keys = Strategy.GenerateKeys(TestRecords.Person("r", new Dictionary<string, string> { ["full_name"] = "John Smith" }), TestProfiles.Person);
        var expected = DoubleMetaphone.Encode("Smith");

        Assert.Contains($"phonetic:{expected.Primary}", keys);
    }

    [Fact]
    public void Phonetic_Organization_EncodesFirstNonStopwordToken()
    {
        var keys = Strategy.GenerateKeys(TestRecords.Person("r", new Dictionary<string, string> { ["organization_name"] = "The Acme Holdings" }), TestProfiles.Person);
        var expected = DoubleMetaphone.Encode("Acme");

        Assert.Contains($"phonetic:{expected.Primary}", keys);
    }

    [Fact]
    public void Phonetic_EmitsAlternate_WhenDistinct()
    {
        // "Bacher" yields distinct primary (K..) and alternate (X..) readings.
        var keys = Strategy.GenerateKeys(TestRecords.Person("r", new Dictionary<string, string> { ["last_name"] = "Bacher" }), TestProfiles.Person);
        var encoded = DoubleMetaphone.Encode("Bacher");

        Assert.Contains($"phonetic:{encoded.Primary}", keys);
        Assert.Contains($"phonetic:{encoded.Alternate}", keys);
    }

    [Fact]
    public void Phonetic_IgnoresIdentifierFields()
    {
        var keys = Strategy.GenerateKeys(TestRecords.Person("r", new Dictionary<string, string> { ["email"] = "alice@example.com" }), TestProfiles.Person);
        Assert.Empty(keys);
    }
}
