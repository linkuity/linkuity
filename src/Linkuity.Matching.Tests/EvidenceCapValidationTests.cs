using System.Globalization;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Profiles.Configuration;
using Linkuity.Matching.Strategies;

namespace Linkuity.Matching.Tests;

public class EvidenceCapValidationTests
{
    // The "evidence" scoring strategy — the source of the LogOdds scale these tests' thresholds
    // and caps are expressed on — is registered in MatchingDefaults.CreateRegistry() alongside the
    // others (stage 1a), so no local registry assembly is needed here anymore.
    private static IStrategyRegistry Registry() => MatchingDefaults.CreateRegistry();

    private static MatchingProfile Load(string json) => new MatchingProfileConfigLoader().LoadFromJson(json, Registry());

    private const string OneFieldOrganizationJsonTemplate = """
    {
      "contentType": "organization",
      "fields": [
        { "name": "organization_name", "semanticType": "OrganizationName", "roles": [%ROLES%], "weight": 1.0, "evidence": { "sameEntityAgreement": 0.9, "chanceAgreement": 0.1%CAP% } }
      ],
      "normalizationStrategy": "identity",
      "blockingStrategies": ["exact-value"],
      "candidateRetrievalStrategy": "linear",
      "similarityStrategy": "field-weighted",
      "scoringStrategy": "evidence",
      "decisionStrategy": "threshold",
      "clusteringStrategy": "union-find",
      "autoMatchThreshold": %AUTO%,
      "reviewThreshold": %REVIEW%
    }
    """;

    private static string ProfileJson(string[] roles, double? maxAgreementBits, double autoMatchThreshold)
    {
        var rolesJson = string.Join(",", roles.Select(r => $"\"{r}\""));
        var cap = maxAgreementBits is { } bits
            ? $", \"maxAgreementBits\": {bits.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;

        return OneFieldOrganizationJsonTemplate
            .Replace("%ROLES%", rolesJson)
            .Replace("%CAP%", cap)
            .Replace("%AUTO%", autoMatchThreshold.ToString(CultureInfo.InvariantCulture))
            .Replace("%REVIEW%", (autoMatchThreshold - 4.0).ToString(CultureInfo.InvariantCulture));
    }

    private static string ProfileJsonWithoutEvidence() => """
        {
          "contentType": "organization",
          "fields": [
            { "name": "organization_name", "semanticType": "OrganizationName", "roles": ["matchable"], "weight": 1.0 }
          ],
          "normalizationStrategy": "identity",
          "blockingStrategies": ["exact-value"],
          "candidateRetrievalStrategy": "linear",
          "similarityStrategy": "field-weighted",
          "scoringStrategy": "evidence",
          "decisionStrategy": "threshold",
          "clusteringStrategy": "union-find",
          "autoMatchThreshold": 8.0,
          "reviewThreshold": 4.0
        }
        """;

    [Fact]
    public void ADescriptiveFieldWithNoCap_IsRejected()
    {
        // Uncapped must be a deliberate declaration, never something that arrives by omission —
        // otherwise the rule below never applies to the field it most needed to.
        var json = ProfileJson(roles: ["matchable"], maxAgreementBits: null, autoMatchThreshold: 8.0);

        var ex = Assert.Throws<MatchingProfileConfigException>(() => Load(json));

        Assert.Contains("maxAgreementBits", ex.Message, StringComparison.Ordinal);
        Assert.Contains("identifier", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AVerifiedIdentifierWithNoCap_IsAllowed()
    {
        // "This alone is sufficient" is precisely an identifier's job.
        var json = ProfileJson(roles: ["matchable", "identifier"], maxAgreementBits: null, autoMatchThreshold: 8.0);

        var profile = Load(json);

        Assert.Null(profile.Fields.Single().Evidence!.MaxAgreementBits);
    }

    [Fact]
    public void ACapThatCouldCarryAMergeAlone_IsRejectedAndNamesBothNumbers()
    {
        var json = ProfileJson(roles: ["matchable"], maxAgreementBits: 9.0, autoMatchThreshold: 8.0);

        var ex = Assert.Throws<MatchingProfileConfigException>(() => Load(json));

        Assert.Contains("9", ex.Message, StringComparison.Ordinal);
        Assert.Contains("8", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACapEqualToTheThreshold_IsRejected()
    {
        // Strictly below: at exactly the threshold a lone agreement reaches the auto band.
        var json = ProfileJson(roles: ["matchable"], maxAgreementBits: 8.0, autoMatchThreshold: 8.0);

        Assert.Throws<MatchingProfileConfigException>(() => Load(json));
    }

    [Fact]
    public void ACapBelowTheThreshold_IsAccepted()
    {
        var json = ProfileJson(roles: ["matchable"], maxAgreementBits: 6.0, autoMatchThreshold: 8.0);

        var profile = Load(json);

        Assert.Equal(6.0, profile.Fields.Single().Evidence!.MaxAgreementBits);
    }

    [Fact]
    public void ProfilesWithoutEvidence_AreUnaffected()
    {
        // Every shipped profile is in this state during stage 1a and must keep loading.
        var profile = Load(ProfileJsonWithoutEvidence());

        Assert.Null(profile.Fields.Single().Evidence);
    }
}
