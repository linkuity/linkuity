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

    private const string MinimalOrganizationJsonWithEvidenceTemplate = """
    {
      "contentType": "organization",
      "fields": [
        { "name": "organization_name", "semanticType": "OrganizationName", "roles": ["Matchable"], "similarityEvaluator": "fuzzy", "weight": 1.0, "evidence": { %EVIDENCE% } }
      ],
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

    private static string ProfileJsonWithFieldEvidence(double sameEntityAgreement, double chanceAgreement)
        => MinimalOrganizationJsonWithEvidenceTemplate.Replace("%EVIDENCE%",
            $"\"sameEntityAgreement\": {sameEntityAgreement}, \"chanceAgreement\": {chanceAgreement}");

    private static string ProfileJsonWithPartialFieldEvidence(double sameEntityAgreement)
        => MinimalOrganizationJsonWithEvidenceTemplate.Replace("%EVIDENCE%",
            $"\"sameEntityAgreement\": {sameEntityAgreement}");

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
    public void LoadFromJson_IdentifierFloorGateAbsent_DefaultsTo035()
    {
        var profile = new MatchingProfileConfigLoader().LoadFromJson(OrganizationJson, Registry());
        Assert.Equal(0.35, profile.IdentifierFloorGate); // absent in JSON -> default
    }

    [Fact]
    public void LoadFromJson_ReadsExplicitIdentifierFloorGate()
    {
        var json = OrganizationJson.Replace(
            "\"reviewThreshold\": 0.75",
            "\"reviewThreshold\": 0.75,\n      \"identifierFloorGate\": 0.5");
        var profile = new MatchingProfileConfigLoader().LoadFromJson(json, Registry());
        Assert.Equal(0.5, profile.IdentifierFloorGate);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("-0.1")]
    public void LoadFromJson_RejectsOutOfRangeIdentifierFloorGate(string value)
    {
        var json = OrganizationJson.Replace(
            "\"reviewThreshold\": 0.75",
            $"\"reviewThreshold\": 0.75,\n      \"identifierFloorGate\": {value}");
        var ex = Assert.Throws<MatchingProfileConfigException>(
            () => new MatchingProfileConfigLoader().LoadFromJson(json, Registry()));
        Assert.Contains("identifierFloorGate", ex.Message);
        Assert.Contains("[0, 1]", ex.Message);
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
    public void EvidenceWithChanceAboveMatchRate_IsRejectedAtLoad_NotAtScoreTime()
    {
        // Lazily validated on the type, eagerly validated here: a bad profile must fail when it
        // is read, not on whichever pair happens to be scored first.
        var json = ProfileJsonWithFieldEvidence(sameEntityAgreement: 0.2, chanceAgreement: 0.5);

        var ex = Assert.Throws<MatchingProfileConfigException>(
            () => new MatchingProfileConfigLoader().LoadFromJson(json, Registry()));

        Assert.Contains("invalid evidence", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvidenceMissingOneProbability_IsRejected()
    {
        var json = ProfileJsonWithPartialFieldEvidence(sameEntityAgreement: 0.9);

        var ex = Assert.Throws<MatchingProfileConfigException>(
            () => new MatchingProfileConfigLoader().LoadFromJson(json, Registry()));

        Assert.Contains("chanceAgreement", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadFromJson_RarityExemptValuesAbsent_DefaultsToEmpty()
    {
        // A frozen measurement baseline depends on shipped profiles that omit this key loading
        // exactly as they did before it existed.
        var profile = new MatchingProfileConfigLoader().LoadFromJson(OrganizationJson, Registry());
        Assert.Empty(profile.RarityExemptValues);
    }

    [Fact]
    public void LoadFromJson_ReadsExplicitRarityExemptValues()
    {
        var json = OrganizationJson.Replace(
            "\"reviewThreshold\": 0.75",
            "\"reviewThreshold\": 0.75,\n      \"rarityExemptValues\": [\"N/A\", \"UNKNOWN\"]");
        var profile = new MatchingProfileConfigLoader().LoadFromJson(json, Registry());
        Assert.Equal(["N/A", "UNKNOWN"], profile.RarityExemptValues);
    }

    [Fact]
    public void LoadFromJson_NullEquivalentsAbsent_DefaultsToNull()
    {
        // No silent default: a field that does not declare nullEquivalents must load exactly as
        // it did before this property existed -- no sentinel is invented for it.
        var profile = new MatchingProfileConfigLoader().LoadFromJson(OrganizationJson, Registry());
        Assert.Null(profile.Fields.Single(f => f.Name == "organization_name").NullEquivalents);
    }

    [Fact]
    public void LoadFromJson_ReadsExplicitNullEquivalents()
    {
        var json = OrganizationJson.Replace(
            "\"similarityEvaluator\": \"fuzzy\", \"weight\": 2.0 },",
            "\"similarityEvaluator\": \"fuzzy\", \"weight\": 2.0, \"nullEquivalents\": [\"8888\", \"UNKNOWN\"] },");

        var profile = new MatchingProfileConfigLoader().LoadFromJson(json, Registry());

        Assert.Equal(["8888", "UNKNOWN"], profile.Fields.Single(f => f.Name == "organization_name").NullEquivalents);
    }

    // Two OrganizationName fields sharing an alias group, %EVIDENCE_A%/%EVIDENCE_B% independently
    // substitutable so one test can keep them identical (accepted) and another can diverge them
    // (rejected) without duplicating the surrounding profile shape.
    private const string TwoFieldAliasGroupJsonTemplate = """
    {
      "contentType": "organization",
      "fields": [
        { "name": "organization_name", "semanticType": "OrganizationName", "roles": ["Matchable"], "weight": 1.0, "aliasGroup": "org_name", "evidence": { %EVIDENCE_A% } },
        { "name": "trade_name",        "semanticType": "OrganizationName", "roles": ["Matchable"], "weight": 1.0, "aliasGroup": "org_name", "evidence": { %EVIDENCE_B% } }
      ],
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

    private const string SharedFieldEvidence = "\"sameEntityAgreement\": 0.9, \"chanceAgreement\": 0.1, \"maxAgreementBits\": 0.5";

    private static string AliasGroupJson(string evidenceA, string evidenceB)
        => TwoFieldAliasGroupJsonTemplate.Replace("%EVIDENCE_A%", evidenceA).Replace("%EVIDENCE_B%", evidenceB);

    [Fact]
    public void LoadFromJson_ReadsAliasGroupFromField()
    {
        // Deleting `AliasGroup = field.AliasGroup` from the loader's field-building code leaves
        // every other test in this file green (I3 finding): nothing exercises the config-loaded
        // path specifically, only hand-built ProfileField objects elsewhere. This asserts the
        // wiring directly, through JSON, the way a real profile file declares it.
        var profile = new MatchingProfileConfigLoader().LoadFromJson(
            AliasGroupJson(SharedFieldEvidence, SharedFieldEvidence), Registry());

        Assert.Equal("org_name", profile.Fields.Single(f => f.Name == "organization_name").AliasGroup);
        Assert.Equal("org_name", profile.Fields.Single(f => f.Name == "trade_name").AliasGroup);
    }

    [Fact]
    public void LoadFromJson_AliasGroupWithDivergentEvidence_IsRejected()
    {
        // Members of one alias group are the same fact and must be priced identically (see the
        // consistency check in MatchingProfileConfigLoader.Build); this exercises that rejection
        // through the config loader rather than through hand-built ProfileField objects, so a
        // regression in the loader's own wiring (not just the check itself) would be caught here.
        var divergent = "\"sameEntityAgreement\": 0.7, \"chanceAgreement\": 0.1, \"maxAgreementBits\": 0.5";

        var ex = Assert.Throws<MatchingProfileConfigException>(
            () => new MatchingProfileConfigLoader().LoadFromJson(
                AliasGroupJson(SharedFieldEvidence, divergent), Registry()));

        Assert.Contains("alias group", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("org_name", ex.Message, StringComparison.Ordinal);
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
