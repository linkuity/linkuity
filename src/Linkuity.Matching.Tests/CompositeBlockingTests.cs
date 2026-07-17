using Linkuity.Core.Models;
using Linkuity.Matching.Phonetics;
using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

public class CompositeBlockingTests
{
    private static readonly IBlockingStrategy Strategy = CompositeBlockingStrategy.DobLastNamePhonetic();

    [Fact]
    public void Composite_CombinesDobAndPhoneticLastName()
    {
        var record = TestRecords.Person("r", new Dictionary<string, string> { ["date_of_birth"] = "1990-01-02", ["last_name"] = "Smith" });
        var primary = DoubleMetaphone.Encode("Smith").Primary;

        var keys = Strategy.GenerateKeys(record, TestProfiles.Person);

        Assert.Equal([$"dob-lastname-phonetic:19900102+{primary}"], keys);
    }

    [Fact]
    public void Composite_NameVariantsWithSameDob_ShareKey()
    {
        var a = Strategy.GenerateKeys(TestRecords.Person("a", new Dictionary<string, string> { ["date_of_birth"] = "1990-01-02", ["last_name"] = "Smith" }), TestProfiles.Person);
        var b = Strategy.GenerateKeys(TestRecords.Person("b", new Dictionary<string, string> { ["date_of_birth"] = "1990-01-02", ["last_name"] = "Smyth" }), TestProfiles.Person);

        Assert.NotEmpty(a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Composite_EmitsNothingWhenAPartIsMissing()
    {
        var noDob = Strategy.GenerateKeys(TestRecords.Person("r", new Dictionary<string, string> { ["last_name"] = "Smith" }), TestProfiles.Person);
        var noName = Strategy.GenerateKeys(TestRecords.Person("r", new Dictionary<string, string> { ["date_of_birth"] = "1990-01-02" }), TestProfiles.Person);

        Assert.Empty(noDob);
        Assert.Empty(noName);
    }

    [Fact]
    public void Composite_HasExpectedName()
        => Assert.Equal("dob-lastname-phonetic", Strategy.Name);
}
