using Linkuity.Matching.Profiles;
using Linkuity.Matching.Profiles.Configuration;

namespace Linkuity.Matching.Tests;

/// <summary>
/// A phone number written without a country code is ambiguous, so the region used to read it is
/// a deployment fact rather than a constant. It was hardcoded to US.
/// </summary>
public class DefaultPhoneRegionConfigTests
{
    private static string ProfileJson(string extra) => $$"""
        {
          "contentType": "person-test",
          "fields": [
            { "name": "phone", "semanticType": "Phone", "roles": ["Matchable","Blocking"] }
          ],
          "normalizationStrategy": "identity",
          "blockingStrategies": ["exact-value"],
          "candidateRetrievalStrategy": "linear",
          "similarityStrategy": "default",
          "scoringStrategy": "default",
          "decisionStrategy": "threshold",
          "clusteringStrategy": "union-find",
          "autoMatchThreshold": 0.9,
          "reviewThreshold": 0.75{{extra}}
        }
        """;

    private static MatchingProfile Load(string extra)
        => new MatchingProfileConfigLoader().LoadFromJson(ProfileJson(extra), MatchingDefaults.CreateRegistry());

    [Fact]
    public void Absent_DefaultsToUS_PreservingPreviousBehaviour()
        => Assert.Equal("US", Load("").DefaultPhoneRegion);

    [Fact]
    public void Present_RoundTrips()
        => Assert.Equal("GB", Load(",\n  \"defaultPhoneRegion\": \"GB\"").DefaultPhoneRegion);

    [Theory]
    [InlineData("\"gb\"")]      // lowercase
    [InlineData("\"GBR\"")]     // ISO 3166-1 alpha-3, not alpha-2
    [InlineData("\"G\"")]
    [InlineData("\"\"")]
    public void Malformed_Throws(string value)
    {
        var ex = Assert.Throws<MatchingProfileConfigException>(
            () => Load($",\n  \"defaultPhoneRegion\": {value}"));
        Assert.Contains("defaultPhoneRegion", ex.Message);
    }

    /// <summary>
    /// A well-formed but unknown region is accepted by the loader and fails closed downstream:
    /// libphonenumber owns the region list, and an unrecognised one leaves numbers un-normalized
    /// rather than assigning them to some other country.
    /// </summary>
    [Fact]
    public void WellFormedButUnknownRegion_IsAccepted()
        => Assert.Equal("ZZ", Load(",\n  \"defaultPhoneRegion\": \"ZZ\"").DefaultPhoneRegion);
}
