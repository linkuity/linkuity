using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Profiles.Configuration;
using Linkuity.Matching.Strategies;

namespace Linkuity.Matching.Tests;

public class MatchingProfileConfigLoaderTests
{
    private static IStrategyRegistry Registry() => MatchingDefaults.CreateRegistry();

    private const string OrganizationJson = """
    {
      "contentType": "organization",
      "fields": [
        { "name": "source",            "semanticType": "SourceIdentifier", "roles": [] },
        { "name": "organization_name", "semanticType": "OrganizationName", "roles": ["Searchable","Matchable","Blocking"], "similarityEvaluator": "fuzzy", "weight": 2.0 },
        { "name": "domain_name",       "semanticType": "DomainName",       "roles": ["Searchable","Matchable","Blocking"], "similarityEvaluator": "exact", "weight": 2.5 },
        { "name": "address_line",      "semanticType": "AddressLine",      "roles": ["Searchable","Matchable"],            "similarityEvaluator": "ngram", "weight": 1.0, "evaluatorOptions": { "ngram.size": "3" } }
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

    [Fact]
    public void LoadFromJson_MapsAllProfileProperties()
    {
        var profile = new MatchingProfileConfigLoader().LoadFromJson(OrganizationJson, Registry());

        Assert.Equal("organization", profile.ContentType);
        Assert.Equal("identity", profile.NormalizationStrategy);
        Assert.Equal(["exact-value", "token-name"], profile.BlockingStrategies);
        Assert.Equal("field-weighted", profile.SimilarityStrategy);
        Assert.Equal("identifier-weighted", profile.ScoringStrategy);
        Assert.Equal(0.90, profile.AutoMatchThreshold);
        Assert.Equal(0.75, profile.ReviewThreshold);
        Assert.Equal(0.75, profile.ReviewFloorGate); // absent in JSON -> default
        Assert.Equal(4, profile.Fields.Count);
    }

    [Fact]
    public void LoadFromJson_MapsFieldRolesSemanticTypeEvaluatorAndOptions()
    {
        var profile = new MatchingProfileConfigLoader().LoadFromJson(OrganizationJson, Registry());

        var source = profile.Fields.Single(f => f.Name == "source");
        Assert.Equal(SemanticFieldType.SourceIdentifier, source.SemanticType);
        Assert.Equal(FieldRole.None, source.Roles);
        Assert.Equal(1.0, source.Weight); // default

        var domain = profile.Fields.Single(f => f.Name == "domain_name");
        Assert.Equal(SemanticFieldType.DomainName, domain.SemanticType);
        Assert.Equal(FieldRole.Searchable | FieldRole.Matchable | FieldRole.Blocking, domain.Roles);
        Assert.Equal("exact", domain.SimilarityEvaluator);
        Assert.Equal(2.5, domain.Weight);

        var address = profile.Fields.Single(f => f.Name == "address_line");
        Assert.NotNull(address.EvaluatorOptions);
        Assert.Equal("3", address.EvaluatorOptions!["ngram.size"]);
    }

    private static string JsonWith(string replaceFrom, string replaceTo)
        => OrganizationJson.Replace(replaceFrom, replaceTo);

    [Theory]
    [InlineData("\"normalizationStrategy\": \"identity\"", "\"normalizationStrategy\": \"no-such-norm\"", "no-such-norm")]
    [InlineData("\"blockingStrategies\": [\"exact-value\", \"token-name\"]", "\"blockingStrategies\": [\"no-such-block\"]", "no-such-block")]
    [InlineData("\"candidateRetrievalStrategy\": \"linear\"", "\"candidateRetrievalStrategy\": \"no-such-retrieval\"", "no-such-retrieval")]
    [InlineData("\"similarityStrategy\": \"field-weighted\"", "\"similarityStrategy\": \"no-such-sim\"", "no-such-sim")]
    [InlineData("\"scoringStrategy\": \"identifier-weighted\"", "\"scoringStrategy\": \"no-such-score\"", "no-such-score")]
    [InlineData("\"decisionStrategy\": \"threshold\"", "\"decisionStrategy\": \"no-such-decision\"", "no-such-decision")]
    [InlineData("\"clusteringStrategy\": \"union-find\"", "\"clusteringStrategy\": \"no-such-cluster\"", "no-such-cluster")]
    [InlineData("\"similarityEvaluator\": \"exact\"", "\"similarityEvaluator\": \"no-such-eval\"", "no-such-eval")]
    [InlineData("\"semanticType\": \"DomainName\"", "\"semanticType\": \"NotAType\"", "NotAType")]
    [InlineData("\"roles\": [\"Searchable\",\"Matchable\",\"Blocking\"]", "\"roles\": [\"Bogus\"]", "Bogus")]
    public void LoadFromJson_RejectsUnknownNames(string from, string to, string offending)
    {
        var ex = Assert.Throws<MatchingProfileConfigException>(
            () => new MatchingProfileConfigLoader().LoadFromJson(JsonWith(from, to), Registry()));
        Assert.Contains(offending, ex.Message);
    }

    [Fact]
    public void LoadFromJson_RejectsAutoBelowReview()
    {
        var json = JsonWith("\"autoMatchThreshold\": 0.90", "\"autoMatchThreshold\": 0.50");
        var ex = Assert.Throws<MatchingProfileConfigException>(
            () => new MatchingProfileConfigLoader().LoadFromJson(json, Registry()));
        Assert.Contains("autoMatchThreshold", ex.Message);
    }

    [Fact]
    public void LoadFromJson_RejectsAutoEqualToReview()
    {
        // The durable store requires autoMatchThreshold > reviewThreshold; reject the
        // equal boundary at load time so the failure is clear rather than surfacing later.
        var json = JsonWith("\"autoMatchThreshold\": 0.90", "\"autoMatchThreshold\": 0.75");
        var ex = Assert.Throws<MatchingProfileConfigException>(
            () => new MatchingProfileConfigLoader().LoadFromJson(json, Registry()));
        Assert.Contains("autoMatchThreshold", ex.Message);
    }

    [Fact]
    public void LoadFromJson_RejectsOutOfRangeThreshold()
    {
        var json = JsonWith("\"reviewThreshold\": 0.75", "\"reviewThreshold\": 1.5");
        Assert.Throws<MatchingProfileConfigException>(
            () => new MatchingProfileConfigLoader().LoadFromJson(json, Registry()));
    }

    [Fact]
    public void LoadFromJson_ReadsExplicitReviewFloorGate()
    {
        var json = OrganizationJson.Replace(
            "\"reviewThreshold\": 0.75",
            "\"reviewThreshold\": 0.75,\n      \"reviewFloorGate\": 0.6");
        var profile = new MatchingProfileConfigLoader().LoadFromJson(json, Registry());
        Assert.Equal(0.6, profile.ReviewFloorGate);
    }

    [Fact]
    public void LoadFromJson_RejectsOutOfRangeReviewFloorGate()
    {
        var json = OrganizationJson.Replace(
            "\"reviewThreshold\": 0.75",
            "\"reviewThreshold\": 0.75,\n      \"reviewFloorGate\": 1.5");
        Assert.Throws<MatchingProfileConfigException>(
            () => new MatchingProfileConfigLoader().LoadFromJson(json, Registry()));
    }

    [Fact]
    public void LoadFromJson_RejectsDuplicateFieldName()
    {
        var json = JsonWith("\"name\": \"domain_name\"", "\"name\": \"source\"");
        var ex = Assert.Throws<MatchingProfileConfigException>(
            () => new MatchingProfileConfigLoader().LoadFromJson(json, Registry()));
        Assert.Contains("source", ex.Message);
    }

    [Fact]
    public void LoadFromFile_ReadsAndValidates()
    {
        var dir = Path.Combine(Path.GetTempPath(), "linkuity-profiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "organization.profile.json");
            File.WriteAllText(path, OrganizationJson);

            var profile = new MatchingProfileConfigLoader().LoadFromFile(path, Registry());

            Assert.Equal("organization", profile.ContentType);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LoadFromFile_ErrorMessageNamesTheFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "linkuity-profiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "bad.profile.json");
            File.WriteAllText(path, OrganizationJson.Replace("\"similarityStrategy\": \"field-weighted\"", "\"similarityStrategy\": \"nope\""));

            var ex = Assert.Throws<MatchingProfileConfigException>(
                () => new MatchingProfileConfigLoader().LoadFromFile(path, Registry()));
            Assert.Contains("bad.profile.json", ex.Message);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LoadFromDirectory_LoadsEveryProfileFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "linkuity-profiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "organization.profile.json"), OrganizationJson);
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "ignored");

            var profiles = new MatchingProfileConfigLoader().LoadFromDirectory(dir, Registry());

            Assert.Single(profiles);
            Assert.Equal("organization", profiles[0].ContentType);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
