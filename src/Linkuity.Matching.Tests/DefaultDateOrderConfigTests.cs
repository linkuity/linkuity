using Linkuity.Core.Normalization;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Profiles.Configuration;

namespace Linkuity.Matching.Tests;

public class DefaultDateOrderConfigTests
{
    private static string ProfileJson(string extra) => $$"""
        {
          "contentType": "person-date-test",
          "fields": [
            { "name": "date_of_birth", "semanticType": "DateOfBirth", "roles": ["Matchable","Blocking"] }
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
    public void Absent_DefaultsToMonthFirst_PreservingPreviousBehaviour()
        => Assert.Equal(DateFieldOrder.MonthFirst, Load("").DefaultDateOrder);

    [Fact]
    public void Present_RoundTrips()
        => Assert.Equal(DateFieldOrder.DayFirst, Load(",\n  \"defaultDateOrder\": \"DayFirst\"").DefaultDateOrder);

    [Fact]
    public void CaseInsensitive()
        => Assert.Equal(DateFieldOrder.DayFirst, Load(",\n  \"defaultDateOrder\": \"dayfirst\"").DefaultDateOrder);

    /// <summary>
    /// A typo must not fall back to the default. Silently reading a day-first feed as month-first
    /// mislabels every date before the thirteenth of a month and reports nothing, so an
    /// unrecognised value has to fail at load rather than at inspection time months later.
    /// </summary>
    [Theory]
    [InlineData("\"DMY\"")]
    [InlineData("\"day-first\"")]
    [InlineData("\"\"")]
    public void Unrecognised_Throws(string value)
    {
        var ex = Assert.Throws<MatchingProfileConfigException>(
            () => Load($",\n  \"defaultDateOrder\": {value}"));
        Assert.Contains("defaultDateOrder", ex.Message);
    }

    /// <summary>The setting reaches normalization, not just the profile object.</summary>
    [Fact]
    public void Setting_ReachesNormalizationSettings()
        => Assert.Equal(
            DateFieldOrder.DayFirst,
            Load(",\n  \"defaultDateOrder\": \"DayFirst\"").NormalizationSettings().DateOrder);
}
