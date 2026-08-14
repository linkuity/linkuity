using Linkuity.Core.Models;
using Linkuity.Matching.Extraction;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Profiles.Configuration;
using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

/// <summary>
/// The legal form lives in an organization name's trailing token, and name canonicalization
/// strips it — correctly, or "THE BOEING COMPANY" and "BOEING CO" stop matching across sources.
/// Stripping is also what lets "ABB AG" and "ABB B.V." compare as the same name. Deriving the
/// form into its own field is how both hold at once, so these tests assert BOTH halves: the
/// derived field separates the corporate-group members, AND name scoring is byte-for-byte
/// unchanged.
/// </summary>
public class DerivedLegalFormFieldTests
{
    private static IStrategyRegistry Registry() => MatchingDefaults.CreateRegistry();

    private const string DerivedJson = """
    {
      "contentType": "organization",
      "fields": [
        { "name": "organization_name", "semanticType": "OrganizationName", "roles": ["Searchable","Matchable","Blocking"], "similarityEvaluator": "canonical-jaccard", "nullEquivalents": ["NAME UNDER CONFIRMATION"] },
        { "name": "legal_form_name",   "semanticType": "LegalForm",        "roles": ["Matchable"], "similarityEvaluator": "exact", "sourceField": "organization_name", "extractor": "org-legal-form" }
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

    /// <summary>The derivation clause as it appears in <see cref="DerivedJson"/>, so tests can
    /// remove or corrupt it without restating the whole document.</summary>
    private const string Derivation = "\"sourceField\": \"organization_name\", \"extractor\": \"org-legal-form\"";

    private static MatchingProfile DerivedProfile() =>
        new MatchingProfileConfigLoader().LoadFromJson(DerivedJson, Registry());

    private static EntityRecord Record(params (string Field, string Value)[] fields) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = Guid.Empty,
        SourceId = Guid.Empty,
        IngestBatchId = Guid.Empty,
        SourceRecordId = "r",
        Fields = fields.ToDictionary(f => f.Field, f => f.Value, StringComparer.OrdinalIgnoreCase),
        CreatedAt = DateTimeOffset.UnixEpoch
    };

    // ---- the extractor -----------------------------------------------------------------

    [Theory]
    [InlineData("ABB AG", "AG")]
    [InlineData("ABB B.V.", "BV")]
    [InlineData("ABB L.L.C", "LLC")]
    [InlineData("ABB LIMITED", "LTD")]
    [InlineData("ALFA LAVAL HOLDING GMBH", "GMBH")]
    [InlineData("ALFA LAVAL KK", "KK")]
    public void Extract_ReadsTheLegalForm(string name, string expected)
        => Assert.Equal(expected, new OrganizationLegalFormExtractor().Extract(name));

    [Theory]
    [InlineData("THE BOEING COMPANY", "BOEING CO")]
    [InlineData("INTEL CORPORATION", "INTEL CORP")]
    [InlineData("TEXAS INSTRUMENTS INCORPORATED", "TEXAS INSTRUMENTS INC")]
    [InlineData("ACME COMPANIES", "acme co.")]
    public void Extract_SpellingVariantsOfOneForm_ShareAClass(string left, string right)
    {
        var extractor = new OrganizationLegalFormExtractor();
        var value = extractor.Extract(left);
        Assert.NotEqual("", value);
        Assert.Equal(value, extractor.Extract(right));
    }

    [Theory]
    [InlineData("SUOMI OY", "SUOMI OYJ")]           // Finnish private vs public: different forms
    [InlineData("ACME LP", "ACME LLP")]             // partnership vs limited-liability partnership
    [InlineData("ACME SA", "ACME SAS")]
    [InlineData("ACME LLC", "ACME PLLC")]
    [InlineData("ABB AG", "ABB B.V.")]
    public void Extract_DifferentForms_DoNotShareAClass(string left, string right)
    {
        var extractor = new OrganizationLegalFormExtractor();
        Assert.NotEqual(extractor.Extract(left), extractor.Extract(right));
    }

    [Fact]
    public void Extract_TakesTheOutermostSuffix()
        => Assert.Equal("INC", new OrganizationLegalFormExtractor().Extract("ACME HOLDINGS INC"));

    [Theory]
    [InlineData("ACME")]              // no suffix at all
    [InlineData("INC")]               // the suffix IS the name; nothing separable to compare
    [InlineData("   ")]
    [InlineData("")]
    [InlineData("-")]
    public void Extract_NothingToDerive_IsEmptyNotAPlaceholder(string name)
        => Assert.Equal("", new OrganizationLegalFormExtractor().Extract(name));

    // ---- name scoring must be untouched ------------------------------------------------

    [Theory]
    [InlineData("THE BOEING COMPANY", "BOEING CO")]
    [InlineData("INTEL CORPORATION", "INTEL CORP")]
    [InlineData("3M COMPANY", "3M CO")]
    [InlineData("STARBUCKS CORPORATION", "STARBUCKS CORP")]
    [InlineData("TEXAS INSTRUMENTS INCORPORATED", "TEXAS INSTRUMENTS INC")]
    [InlineData("THE WALT DISNEY COMPANY", "Walt Disney Co")]
    public void NameSimilarity_SuffixVariants_StillScoreOne(string left, string right)
    {
        var field = new ProfileField
        {
            Name = "organization_name",
            SemanticType = SemanticFieldType.OrganizationName,
            Roles = FieldRole.Matchable
        };
        Assert.Equal(1.0, new CanonicalJaccardSimilarityEvaluator().Evaluate(left, right, field)!.Value, 10);
    }

    [Fact]
    public void NameSimilarity_SharedSuffixDoesNotCreateSimilarity()
    {
        // The property option "B" would have broken: both names end in COMPANY, and folding a
        // shared legal-form token into the name would score these unrelated companies above zero.
        var field = new ProfileField
        {
            Name = "organization_name",
            SemanticType = SemanticFieldType.OrganizationName,
            Roles = FieldRole.Matchable
        };
        Assert.Equal(0.0,
            new CanonicalJaccardSimilarityEvaluator()
                .Evaluate("THE WALT DISNEY COMPANY", "THE BOEING COMPANY", field)!.Value, 10);
    }

    // ---- derivation through the normalization seam -------------------------------------

    [Fact]
    public void Resolve_DerivesEvenUnderIdentityNormalization()
    {
        // identity returns the record untouched, which is exactly why derivation cannot live
        // inside a normalization strategy: the shipped organization profiles declare it.
        var profile = DerivedProfile();
        var normalized = ProfileNormalization.Resolve(Registry(), profile)
            .Normalize(Record(("organization_name", "ABB AG")), profile);

        Assert.Equal("AG", normalized.Fields["legal_form_name"]);
        Assert.Equal("ABB AG", normalized.Fields["organization_name"]);
    }

    [Fact]
    public void Resolve_ReportsTheProfilesOwnStrategyName()
        => Assert.Equal("identity", ProfileNormalization.Resolve(Registry(), DerivedProfile()).Name);

    [Fact]
    public void Resolve_NoDerivedFields_ReturnsTheStrategyUnchanged()
    {
        var registry = Registry();
        var profile = new MatchingProfileConfigLoader()
            .LoadFromJson(DerivedJson.Replace(", " + Derivation, ""), registry);

        Assert.DoesNotContain(profile.Fields, f => f.IsDerived);
        Assert.Same(registry.Normalization[profile.NormalizationStrategy],
                    ProfileNormalization.Resolve(registry, profile));
    }

    [Fact]
    public void Resolve_SentinelSource_DerivesNothing()
    {
        // A placeholder name carries no legal form. Deriving one would manufacture agreement
        // between every record whose name was never recorded.
        var profile = DerivedProfile();
        var normalized = ProfileNormalization.Resolve(Registry(), profile)
            .Normalize(Record(("organization_name", "Name Under Confirmation")), profile);

        Assert.Equal("", normalized.Fields["legal_form_name"]);
    }

    [Fact]
    public void Resolve_DerivedValueOverwritesAnIngestedColumnOfTheSameName()
    {
        var profile = DerivedProfile();
        var normalized = ProfileNormalization.Resolve(Registry(), profile)
            .Normalize(Record(("organization_name", "ABB AG"), ("legal_form_name", "STALE")), profile);

        Assert.Equal("AG", normalized.Fields["legal_form_name"]);
    }

    [Fact]
    public void CorporateGroupMembers_AgreeOnNameAndDisagreeOnLegalForm()
    {
        // The case this whole change exists for: ten distinct LEIs at one Zurich address whose
        // names differ only in the token canonicalization removes.
        var profile = DerivedProfile();
        var normalization = ProfileNormalization.Resolve(Registry(), profile);
        var ag = normalization.Normalize(Record(("organization_name", "ABB AG")), profile);
        var bv = normalization.Normalize(Record(("organization_name", "ABB B.V.")), profile);

        var nameField = profile.Fields.Single(f => f.Name == "organization_name");
        Assert.Equal(1.0, new CanonicalJaccardSimilarityEvaluator()
            .Evaluate(ag.Fields["organization_name"], bv.Fields["organization_name"], nameField)!.Value, 10);

        Assert.NotEqual(ag.Fields["legal_form_name"], bv.Fields["legal_form_name"]);
    }

    // ---- profile loading ---------------------------------------------------------------

    [Fact]
    public void Load_DerivedField_IsMapped()
    {
        var field = DerivedProfile().Fields.Single(f => f.Name == "legal_form_name");
        Assert.True(field.IsDerived);
        Assert.Equal("organization_name", field.SourceField);
        Assert.Equal("org-legal-form", field.Extractor);
    }

    [Theory]
    // sourceField without extractor, and extractor without sourceField
    [InlineData(Derivation, "\"sourceField\": \"organization_name\"", "declares only one of")]
    [InlineData(Derivation, "\"extractor\": \"org-legal-form\"", "declares only one of")]
    [InlineData("\"extractor\": \"org-legal-form\"", "\"extractor\": \"nope\"", "unknown extractor")]
    [InlineData("\"sourceField\": \"organization_name\"", "\"sourceField\": \"absent_column\"",
                "which the profile does not declare")]
    [InlineData("\"sourceField\": \"organization_name\"", "\"sourceField\": \"legal_form_name\"",
                "derives from itself")]
    public void Load_InvalidDerivation_Throws(string find, string replace, string expected)
    {
        var json = DerivedJson.Replace(find, replace);
        Assert.NotEqual(DerivedJson, json);   // guards the test itself against a stale find string

        var ex = Assert.Throws<MatchingProfileConfigException>(
            () => new MatchingProfileConfigLoader().LoadFromJson(json, Registry()));
        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_ChainedDerivation_Throws()
    {
        const string anchor = "\"extractor\": \"org-legal-form\" }";
        const string plusSecondHop = anchor +
            ", { \"name\": \"second_hop\", \"semanticType\": \"LegalForm\", \"roles\": [\"Matchable\"], " +
            "\"similarityEvaluator\": \"exact\", \"sourceField\": \"legal_form_name\", " +
            "\"extractor\": \"org-legal-form\" }";

        var json = DerivedJson.Replace(anchor, plusSecondHop);
        Assert.NotEqual(DerivedJson, json);

        var ex = Assert.Throws<MatchingProfileConfigException>(
            () => new MatchingProfileConfigLoader().LoadFromJson(json, Registry()));
        Assert.Contains("one level deep", ex.Message);
    }

    // ---- fingerprint -------------------------------------------------------------------

    [Fact]
    public void Fingerprint_ChangesWhenDerivationChanges()
    {
        var withDerivation = DerivedProfile();
        var withoutDerivation = withDerivation with
        {
            Fields = withDerivation.Fields
                .Select(f => f.IsDerived ? f with { SourceField = null, Extractor = null } : f)
                .ToList()
        };

        Assert.NotEqual(ProfileFingerprint.Of(withDerivation), ProfileFingerprint.Of(withoutDerivation));
    }
}
