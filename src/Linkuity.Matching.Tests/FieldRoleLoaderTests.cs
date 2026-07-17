using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Profiles.Configuration;

namespace Linkuity.Matching.Tests;

public class FieldRoleLoaderTests
{
    private const string IdentifierRoleJson = """
    {
      "contentType": "widget",
      "fields": [
        { "name": "serial", "semanticType": "SourceIdentifier", "roles": ["Matchable","Blocking","Identifier"], "similarityEvaluator": "exact" },
        { "name": "label",  "semanticType": "OrganizationName", "roles": ["Matchable","Blocking"], "similarityEvaluator": "fuzzy" }
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
    public void IdentifierRole_IsADistinctComposableFlag()
    {
        Assert.NotEqual(FieldRole.None, FieldRole.Identifier);
        Assert.NotEqual(FieldRole.Blocking, FieldRole.Identifier);
        var combined = FieldRole.Matchable | FieldRole.Blocking | FieldRole.Identifier;
        Assert.True(combined.HasFlag(FieldRole.Identifier));
        Assert.True(combined.HasFlag(FieldRole.Blocking));
        Assert.Equal(8, (int)FieldRole.Identifier);
        Assert.NotEqual(FieldRole.Searchable, FieldRole.Identifier);
        Assert.NotEqual(FieldRole.Matchable, FieldRole.Identifier);
    }

    [Fact]
    public void LoadFromJson_ParsesIdentifierRole()
    {
        var profile = new MatchingProfileConfigLoader().LoadFromJson(IdentifierRoleJson, MatchingDefaults.CreateRegistry());
        var serial = profile.Fields.Single(f => f.Name == "serial");
        Assert.True(serial.Roles.HasFlag(FieldRole.Identifier));
        var label = profile.Fields.Single(f => f.Name == "label");
        Assert.False(label.Roles.HasFlag(FieldRole.Identifier));
    }

    [Theory]
    [InlineData("Sku")]
    [InlineData("Gtin")]
    [InlineData("ProductName")]
    public void LoadFromJson_ParsesProductSemanticTypes(string semanticType)
    {
        var json = $$"""
        {
          "contentType": "product",
          "fields": [ { "name": "f", "semanticType": "{{semanticType}}", "roles": ["Matchable","Blocking","Identifier"], "similarityEvaluator": "exact" } ],
          "normalizationStrategy": "identity",
          "blockingStrategies": ["exact-value"],
          "candidateRetrievalStrategy": "linear",
          "similarityStrategy": "field-weighted",
          "scoringStrategy": "identifier-weighted",
          "decisionStrategy": "threshold",
          "clusteringStrategy": "union-find",
          "autoMatchThreshold": 0.90,
          "reviewThreshold": 0.75
        }
        """;
        var profile = new MatchingProfileConfigLoader().LoadFromJson(json, MatchingDefaults.CreateRegistry());
        Assert.Single(profile.Fields);
    }
}
