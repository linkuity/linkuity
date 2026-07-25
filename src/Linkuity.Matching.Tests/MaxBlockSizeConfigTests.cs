using Linkuity.Matching;
using Linkuity.Matching.Profiles.Configuration;

namespace Linkuity.Matching.Tests;

public class MaxBlockSizeConfigTests
{
    private static string ProfileJson(string extra) => $$"""
        {
          "contentType": "org-test",
          "fields": [
            { "name": "organization_name", "semanticType": "OrganizationName", "roles": ["Matchable","Blocking"] }
          ],
          "normalizationStrategy": "identity",
          "blockingStrategies": ["token-name"],
          "candidateRetrievalStrategy": "linear",
          "similarityStrategy": "default",
          "scoringStrategy": "default",
          "decisionStrategy": "threshold",
          "clusteringStrategy": "union-find",
          "autoMatchThreshold": 0.9,
          "reviewThreshold": 0.75{{extra}}
        }
        """;

    [Fact]
    public void MaxBlockSize_Absent_LoadsAsNull()
    {
        var profile = new MatchingProfileConfigLoader().LoadFromJson(ProfileJson(""), MatchingDefaults.CreateRegistry());
        Assert.Null(profile.MaxBlockSize);
    }

    [Fact]
    public void MaxBlockSize_Present_RoundTrips()
    {
        var profile = new MatchingProfileConfigLoader().LoadFromJson(ProfileJson(",\n  \"maxBlockSize\": 50"), MatchingDefaults.CreateRegistry());
        Assert.Equal(50, profile.MaxBlockSize);
    }

    [Fact]
    public void MaxBlockSize_BelowOne_Throws()
    {
        var ex = Assert.Throws<MatchingProfileConfigException>(() =>
            new MatchingProfileConfigLoader().LoadFromJson(ProfileJson(",\n  \"maxBlockSize\": 0"), MatchingDefaults.CreateRegistry()));
        Assert.Contains("maxBlockSize", ex.Message);
    }

    [Fact]
    public void WithCandidateRetrievalStrategy_PreservesMaxBlockSize()
    {
        var profile = new MatchingProfileConfigLoader().LoadFromJson(ProfileJson(",\n  \"maxBlockSize\": 7"), MatchingDefaults.CreateRegistry());
        Assert.Equal(7, profile.WithCandidateRetrievalStrategy("blocking-linear").MaxBlockSize);
    }

    [Fact]
    public void BuiltInProfiles_LeaveMaxBlockSizeUnset()
    {
        foreach (var p in Linkuity.Matching.Profiles.DefaultMatchingProfileProvider.BuiltInProfiles())
            Assert.Null(p.MaxBlockSize);
    }
}
