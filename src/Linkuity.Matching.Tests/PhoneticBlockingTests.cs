using Linkuity.Core.Models;
using Linkuity.Matching.Phonetics;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

public class PhoneticBlockingTests
{
    private static readonly IBlockingStrategy Strategy = new PhoneticBlockingStrategy();

    private static MatchingProfile PersonWithLastNameSentinel(params string[] sentinels) => TestProfiles.Person with
    {
        Fields = TestProfiles.Person.Fields
            .Select(f => f.Name == "last_name" ? f with { NullEquivalents = sentinels } : f)
            .ToList()
    };

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
    public void Phonetic_Organization_EncodesFirstCanonicalToken()
    {
        var keys = Strategy.GenerateKeys(TestRecords.Person("r", new Dictionary<string, string> { ["organization_name"] = "The Acme Holdings" }), TestProfiles.Person);
        var expected = DoubleMetaphone.Encode("Acme");

        Assert.Contains($"phonetic:{expected.Primary}", keys);
    }

    [Fact]
    public void Phonetic_Organization_SubsetNames_ShareKey()
    {
        // The prefix-unique showcase class: APPLE COMPUTER INC vs APPLE INC must share a phonetic key so coverage does not depend on prefix alone.
        var longer = Strategy.GenerateKeys(TestRecords.Person("a", new Dictionary<string, string> { ["organization_name"] = "APPLE COMPUTER INC" }), TestProfiles.Person);
        var shorter = Strategy.GenerateKeys(TestRecords.Person("b", new Dictionary<string, string> { ["organization_name"] = "APPLE INC" }), TestProfiles.Person);

        Assert.NotEmpty(longer);
        Assert.NotEmpty(longer.Intersect(shorter));
    }

    [Fact]
    public void Phonetic_Organization_AmpersandInitials_UseCollapsedToken()
    {
        var keys = Strategy.GenerateKeys(TestRecords.Person("r", new Dictionary<string, string> { ["organization_name"] = "AT&T Inc" }), TestProfiles.Person);
        var expected = DoubleMetaphone.Encode("ATT");

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

    /// <summary>
    /// This strategy reads record.Fields directly rather than through BlockingFields.Select, so
    /// its own blank check has to honour NullEquivalents independently -- otherwise a sentinel
    /// last_name would phonetically encode as a real name and collapse every record carrying it
    /// into one block.
    /// </summary>
    [Fact]
    public void Phonetic_DeclaredSentinelValue_NoKeys()
    {
        var keys = Strategy.GenerateKeys(
            TestRecords.Person("r", new Dictionary<string, string> { ["last_name"] = "UNKNOWN" }),
            PersonWithLastNameSentinel("UNKNOWN"));

        Assert.Empty(keys);
    }
}
