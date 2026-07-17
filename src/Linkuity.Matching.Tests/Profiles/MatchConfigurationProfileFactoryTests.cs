using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Tests.Profiles;

public sealed class MatchConfigurationProfileFactoryTests
{
    private static MatchConfiguration PersonConfig(params Field[] fields) =>
        new() { ContentType = "person", Fields = fields };

    private static Field F(string name, SemanticFieldType type, bool participates = true) =>
        new() { Name = name, SemanticType = type, ParticipatesInMatching = participates };

    [Fact]
    public void Create_OmitsFieldsThatDoNotParticipate()
    {
        var config = PersonConfig(
            F("first_name", SemanticFieldType.FirstName),
            F("phone", SemanticFieldType.Phone, participates: false));

        var profile = MatchConfigurationProfileFactory.Create(config);

        Assert.Contains(profile.Fields, f => f.Name == "first_name");
        Assert.DoesNotContain(profile.Fields, f => f.Name == "phone");
    }

    [Fact]
    public void Create_OmitsSourceIdentifier()
    {
        var config = PersonConfig(
            F("source", SemanticFieldType.SourceIdentifier),
            F("email", SemanticFieldType.Email));

        var profile = MatchConfigurationProfileFactory.Create(config);

        Assert.DoesNotContain(profile.Fields, f => f.SemanticType == SemanticFieldType.SourceIdentifier);
        Assert.Single(profile.Fields);
    }

    [Fact]
    public void Create_PreservesConfigColumnNameEvenWhenItDiffersFromBuiltIn()
    {
        // "company" carries semanticType organization_name; the built-in person profile's
        // org field is named "organization_name". The synthesized field must use "company".
        var config = PersonConfig(F("company", SemanticFieldType.OrganizationName));

        var profile = MatchConfigurationProfileFactory.Create(config);

        var field = Assert.Single(profile.Fields);
        Assert.Equal("company", field.Name);
        Assert.Equal(SemanticFieldType.OrganizationName, field.SemanticType);
    }

    [Fact]
    public void Create_CopiesRoleEvaluatorAndWeightFromBuiltInBySemanticType()
    {
        var config = PersonConfig(F("email", SemanticFieldType.Email));

        var profile = MatchConfigurationProfileFactory.Create(config);

        var email = Assert.Single(profile.Fields);
        // Built-in person email: Searchable|Matchable|Blocking|Identifier, "exact", weight 3.0
        Assert.True(email.Roles.HasFlag(FieldRole.Matchable));
        Assert.True(email.Roles.HasFlag(FieldRole.Identifier));
        Assert.Equal("exact", email.SimilarityEvaluator);
        Assert.Equal(3.0, email.Weight);
    }

    [Fact]
    public void Create_UsesBlockingGatedRetrievalAndBuiltInThresholds()
    {
        var profile = MatchConfigurationProfileFactory.Create(
            PersonConfig(F("email", SemanticFieldType.Email)));

        Assert.Equal("blocking-linear", profile.CandidateRetrievalStrategy);
        Assert.Equal("identifier-weighted", profile.ScoringStrategy);
        Assert.Equal("field-weighted", profile.SimilarityStrategy);
        Assert.Equal(0.90, profile.AutoMatchThreshold);
        Assert.Equal(0.75, profile.ReviewThreshold);
    }

    [Fact]
    public void Create_ResolvesOrganizationContentType()
    {
        var config = new MatchConfiguration
        {
            ContentType = "organization",
            Fields = [F("domain_name", SemanticFieldType.DomainName)]
        };

        var profile = MatchConfigurationProfileFactory.Create(config);

        Assert.Equal("organization", profile.ContentType);
        var domain = Assert.Single(profile.Fields);
        Assert.True(domain.Roles.HasFlag(FieldRole.Identifier)); // org domain is a strong identifier
    }

    [Fact]
    public void Create_ThrowsWhenNoFieldsParticipate()
    {
        var config = PersonConfig(F("source", SemanticFieldType.SourceIdentifier));
        Assert.Throws<ArgumentException>(() => MatchConfigurationProfileFactory.Create(config));
    }

    [Fact]
    public void Create_SynthesizesFallbackFieldWhenSemanticTypeIsNotInBuiltInProfile()
    {
        // Sku is a product-only semantic type; the built-in person profile has no field
        // of this SemanticType, so the factory must fall back to the synthesized default
        // (Searchable|Matchable, "exact", weight 1.0, no Blocking/Identifier role).
        var config = PersonConfig(F("sku", SemanticFieldType.Sku));

        var profile = MatchConfigurationProfileFactory.Create(config);

        var field = Assert.Single(profile.Fields);
        Assert.Equal("sku", field.Name);
        Assert.Equal("exact", field.SimilarityEvaluator);
        Assert.Equal(1.0, field.Weight);
        Assert.Equal(FieldRole.Searchable | FieldRole.Matchable, field.Roles);
    }
}
