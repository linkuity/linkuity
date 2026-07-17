using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Tests;

public class ProfileProviderTests
{
    [Fact]
    public void Provider_ReturnsPersonProfileByContentType()
    {
        var provider = new DefaultMatchingProfileProvider([DefaultMatchingProfileProvider.CreatePersonProfile()]);
        var profile = provider.GetProfile("person");
        Assert.Equal("person", profile.ContentType);
        Assert.Equal(0.90, profile.AutoMatchThreshold);
        Assert.Equal(0.75, profile.ReviewThreshold);
        Assert.Equal(["exact-value", "token-name"], profile.BlockingStrategies);
        Assert.Equal("identity", profile.NormalizationStrategy);
        Assert.Equal("linear", profile.CandidateRetrievalStrategy);
        Assert.Equal("union-find", profile.ClusteringStrategy);
    }

    [Fact]
    public void PersonProfile_MarksIdentifierFieldsAsBlocking()
    {
        var profile = DefaultMatchingProfileProvider.CreatePersonProfile();
        var email = Assert.Single(profile.Fields, f => f.Name == "email");
        Assert.True(email.Roles.HasFlag(FieldRole.Blocking));
        Assert.Equal(SemanticFieldType.Email, email.SemanticType);
    }

    [Fact]
    public void PersonProfile_UsesWeightedStrategiesAndPerFieldEvaluators()
    {
        var profile = DefaultMatchingProfileProvider.CreatePersonProfile();
        Assert.Equal("field-weighted", profile.SimilarityStrategy);
        Assert.Equal("identifier-weighted", profile.ScoringStrategy);

        var email = Assert.Single(profile.Fields, f => f.Name == "email");
        Assert.Equal("exact", email.SimilarityEvaluator);
        Assert.True(email.Weight > 1.0);

        var lastName = Assert.Single(profile.Fields, f => f.Name == "last_name");
        Assert.Equal("fuzzy", lastName.SimilarityEvaluator);
    }

    [Fact]
    public void ParityPersonProfile_KeepsDefaultStrategies()
    {
        var profile = DefaultMatchingProfileProvider.CreateParityPersonProfile();
        Assert.Equal("default", profile.SimilarityStrategy);
        Assert.Equal("default", profile.ScoringStrategy);
        Assert.All(profile.Fields, f => Assert.Equal(1.0, f.Weight));
    }

    [Fact]
    public void Provider_ThrowsForUnknownContentType()
    {
        var provider = new DefaultMatchingProfileProvider([DefaultMatchingProfileProvider.CreatePersonProfile()]);
        Assert.Throws<KeyNotFoundException>(() => provider.GetProfile("organization"));
        Assert.False(provider.TryGetProfile("organization", out _));
    }

    [Fact]
    public void OrganizationProfile_MirrorsCanonicalConfiguration()
    {
        var profile = DefaultMatchingProfileProvider.CreateOrganizationProfile();
        Assert.Equal("organization", profile.ContentType);
        Assert.Equal("identity", profile.NormalizationStrategy);
        Assert.Equal(["exact-value", "token-name"], profile.BlockingStrategies);
        Assert.Equal("field-weighted", profile.SimilarityStrategy);
        Assert.Equal("identifier-weighted", profile.ScoringStrategy);
        Assert.Equal("union-find", profile.ClusteringStrategy);
        Assert.Equal(0.90, profile.AutoMatchThreshold);
        Assert.Equal(0.75, profile.ReviewThreshold);

        var domain = Assert.Single(profile.Fields, f => f.Name == "domain_name");
        Assert.Equal(SemanticFieldType.DomainName, domain.SemanticType);
        Assert.Equal("exact", domain.SimilarityEvaluator);
        Assert.True(domain.Roles.HasFlag(FieldRole.Blocking));
        Assert.True(domain.Roles.HasFlag(FieldRole.Identifier));
        var email = Assert.Single(profile.Fields, f => f.Name == "email");
        Assert.True(email.Roles.HasFlag(FieldRole.Identifier));

        var source = Assert.Single(profile.Fields, f => f.Name == "source");
        Assert.Equal(FieldRole.None, source.Roles);
        Assert.Equal(SemanticFieldType.SourceIdentifier, source.SemanticType);
    }

    [Fact]
    public void BuiltInProfiles_ContainsPersonAndOrganization()
    {
        var contentTypes = DefaultMatchingProfileProvider.BuiltInProfiles()
            .Select(p => p.ContentType)
            .ToArray();
        Assert.Equal(["person", "organization"], contentTypes);
    }

    [Fact]
    public void LoadedProfile_OverridesBuiltInOfSameContentType()
    {
        var provider = new DefaultMatchingProfileProvider(
            DefaultMatchingProfileProvider.BuiltInProfiles(),
            [OverrideOrg(0.85)]);

        Assert.Equal(0.85, provider.GetProfile("organization").ReviewThreshold);
        Assert.Equal("person", provider.GetProfile("person").ContentType); // person untouched
    }

    [Fact]
    public void TwoLoadedProfilesForSameContentType_Throw()
    {
        var ex = Assert.Throws<ArgumentException>(() => new DefaultMatchingProfileProvider(
            DefaultMatchingProfileProvider.BuiltInProfiles(),
            [OverrideOrg(0.85), OverrideOrg(0.88)]));
        Assert.Contains("organization", ex.Message);
    }

    [Fact]
    public void GetProfile_UnknownType_ListsRegisteredProfilesInMessage()
    {
        var provider = new DefaultMatchingProfileProvider(
            DefaultMatchingProfileProvider.BuiltInProfiles(), []);
        var ex = Assert.Throws<KeyNotFoundException>(() => provider.GetProfile("widget"));
        Assert.Contains("widget", ex.Message);
        Assert.Contains("organization", ex.Message);
        Assert.Contains("person", ex.Message);
    }

    private static MatchingProfile OverrideOrg(double reviewThreshold)
    {
        var canonical = DefaultMatchingProfileProvider.CreateOrganizationProfile();
        return new MatchingProfile
        {
            ContentType = canonical.ContentType,
            Fields = canonical.Fields,
            NormalizationStrategy = canonical.NormalizationStrategy,
            BlockingStrategies = canonical.BlockingStrategies,
            CandidateRetrievalStrategy = canonical.CandidateRetrievalStrategy,
            SimilarityStrategy = canonical.SimilarityStrategy,
            ScoringStrategy = canonical.ScoringStrategy,
            DecisionStrategy = canonical.DecisionStrategy,
            ClusteringStrategy = canonical.ClusteringStrategy,
            AutoMatchThreshold = canonical.AutoMatchThreshold,
            ReviewThreshold = reviewThreshold
        };
    }
}
