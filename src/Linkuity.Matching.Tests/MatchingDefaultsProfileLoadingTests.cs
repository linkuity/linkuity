using Linkuity.Matching.DependencyInjection;
using Linkuity.Matching.Profiles;
using Microsoft.Extensions.DependencyInjection;

namespace Linkuity.Matching.Tests;

public sealed class MatchingDefaultsProfileLoadingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"linkuity-di-profiles-{Guid.NewGuid():N}");

    [Fact]
    public void Defaults_RegisterPersonAndOrganizationBuiltIns()
    {
        using var provider = new ServiceCollection().AddLinkuityMatchingDefaults().BuildServiceProvider();
        var profiles = provider.GetRequiredService<IMatchingProfileProvider>();
        Assert.Equal("person", profiles.GetProfile("person").ContentType);
        Assert.Equal("organization", profiles.GetProfile("organization").ContentType);
    }

    [Fact]
    public void LoadProfilesFrom_OverridesBuiltInOrganization()
    {
        Directory.CreateDirectory(_dir);
        var profilePath = Path.Combine(_dir, "organization.profile.json");
        File.WriteAllText(profilePath, OverrideOrgJson);

        using var provider = new ServiceCollection()
            .AddLinkuityMatchingDefaults(o => o.LoadProfilesFrom(profilePath))
            .BuildServiceProvider();

        var org = provider.GetRequiredService<IMatchingProfileProvider>().GetProfile("organization");
        Assert.Equal(0.85, org.ReviewThreshold);
        // person remains the built-in
        Assert.Equal("person", provider.GetRequiredService<IMatchingProfileProvider>().GetProfile("person").ContentType);
    }

    private const string OverrideOrgJson = """
    {
      "contentType": "organization",
      "fields": [
        { "name": "organization_name", "semanticType": "OrganizationName", "roles": ["Searchable","Matchable","Blocking"], "similarityEvaluator": "fuzzy", "weight": 2.0 },
        { "name": "domain_name",       "semanticType": "DomainName",       "roles": ["Searchable","Matchable","Blocking"], "similarityEvaluator": "exact", "weight": 2.5 }
      ],
      "normalizationStrategy": "identity",
      "blockingStrategies": ["exact-value", "token-name"],
      "candidateRetrievalStrategy": "linear",
      "similarityStrategy": "field-weighted",
      "scoringStrategy": "identifier-weighted",
      "decisionStrategy": "threshold",
      "clusteringStrategy": "union-find",
      "autoMatchThreshold": 0.90,
      "reviewThreshold": 0.85
    }
    """;

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
