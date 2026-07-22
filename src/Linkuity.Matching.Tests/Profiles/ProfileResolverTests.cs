using Linkuity.Matching.Profiles;
using Linkuity.Matching.Profiles.Configuration;
using Xunit;

namespace Linkuity.Matching.Tests.Profiles;

public sealed class ProfileResolverTests
{
    [Fact]
    public void ResolveNameOrFile_BuiltInName_ReturnsBuiltIn()
    {
        var profile = ProfileResolver.ResolveNameOrFile("person");
        Assert.Equal("person", profile.ContentType);
    }

    [Fact]
    public void ResolveNameOrFile_FilePath_LoadsFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loc-{Guid.NewGuid():N}.profile.json");
        File.WriteAllText(path, LocationProfileJson);
        try
        {
            var profile = ProfileResolver.ResolveNameOrFile(path);
            Assert.Equal("location", profile.ContentType);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ResolveNameOrFile_UnknownName_ThrowsWithBuiltInsListed()
    {
        var ex = Assert.Throws<MatchingProfileConfigException>(
            () => ProfileResolver.ResolveNameOrFile("nonexistent"));
        Assert.Contains("person", ex.Message);
        Assert.Contains("organization", ex.Message);
    }

    [Fact]
    public void ResolveNameOrJson_BuiltInName_ReturnsBuiltIn()
    {
        var profile = ProfileResolver.ResolveNameOrJson("organization");
        Assert.Equal("organization", profile.ContentType);
    }

    [Fact]
    public void ResolveNameOrJson_ProfileJson_ParsesIt()
    {
        var profile = ProfileResolver.ResolveNameOrJson(LocationProfileJson);
        Assert.Equal("location", profile.ContentType);
    }

    private const string LocationProfileJson = """
    {
      "contentType": "location",
      "fields": [
        { "name": "source", "semanticType": "SourceIdentifier", "roles": [] },
        { "name": "phone", "semanticType": "Phone", "roles": ["Matchable","Blocking","Identifier"], "similarityEvaluator": "exact", "weight": 3.0 }
      ],
      "normalizationStrategy": "identity",
      "blockingStrategies": ["exact-value", "token-name"],
      "candidateRetrievalStrategy": "linear",
      "similarityStrategy": "field-weighted",
      "scoringStrategy": "identifier-weighted",
      "decisionStrategy": "threshold",
      "clusteringStrategy": "union-find",
      "autoMatchThreshold": 0.90,
      "reviewThreshold": 0.75
    }
    """;
}
