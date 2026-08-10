using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

/// <summary>
/// Both overloads of the internal <see cref="BlockingFields.Select"/> helper must honour a
/// field's declared <see cref="ProfileField.NullEquivalents"/> exactly like a blank value: a
/// sentinel must never become a blocking key, or every record sharing it — 400,000 of them, for
/// legal_form = "8888" — would collapse into one block even though the scorer correctly treats
/// them as not agreeing.
/// </summary>
public class BlockingFieldsTests
{
    private static ProfileField Field(string name, SemanticFieldType type, IReadOnlyList<string>? nullEquivalents = null)
        => new()
        {
            Name = name,
            SemanticType = type,
            Roles = FieldRole.Matchable | FieldRole.Blocking,
            NullEquivalents = nullEquivalents
        };

    private static MatchingProfile Profile(params ProfileField[] fields) => new()
    {
        ContentType = "organization",
        Fields = fields,
        NormalizationStrategy = "identity",
        BlockingStrategies = ["exact-value"],
        CandidateRetrievalStrategy = "linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = "identifier-weighted",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 0.90,
        ReviewThreshold = 0.75
    };

    private static EntityRecord Record(Dictionary<string, string> fields) => new()
    {
        Id = Guid.NewGuid(), ProjectId = Guid.Empty, SourceId = Guid.Empty, IngestBatchId = Guid.Empty,
        SourceRecordId = "r", Fields = fields, CreatedAt = DateTimeOffset.UnixEpoch
    };

    [Fact]
    public void TypePredicateOverload_DeclaredSentinel_EmitsNoKey()
    {
        var field = Field("legal_form", SemanticFieldType.LegalForm, ["8888"]);
        var profile = Profile(field);
        var record = Record(new() { ["legal_form"] = "8888" });

        var selected = BlockingFields.Select(record, profile, (SemanticFieldType t) => t == SemanticFieldType.LegalForm);

        Assert.Empty(selected);
    }

    [Fact]
    public void TypePredicateOverload_NonSentinelValue_StillEmitsAKey()
    {
        var field = Field("legal_form", SemanticFieldType.LegalForm, ["8888"]);
        var profile = Profile(field);
        var record = Record(new() { ["legal_form"] = "LLC" });

        var selected = BlockingFields.Select(record, profile, (SemanticFieldType t) => t == SemanticFieldType.LegalForm);

        Assert.Equal([("legal_form", "LLC")], selected);
    }

    [Fact]
    public void TypePredicateOverload_SentinelComparison_IsCaseAndTrimInsensitive()
    {
        var field = Field("legal_form", SemanticFieldType.LegalForm, ["8888"]);
        var profile = Profile(field);
        var record = Record(new() { ["legal_form"] = " 8888 " });

        var selected = BlockingFields.Select(record, profile, (SemanticFieldType t) => t == SemanticFieldType.LegalForm);

        Assert.Empty(selected);
    }

    [Fact]
    public void FieldPredicateOverload_DeclaredSentinel_EmitsNoKey()
    {
        var field = Field("legal_form", SemanticFieldType.LegalForm, ["8888"]) with { Roles = FieldRole.Blocking | FieldRole.Identifier };
        var profile = Profile(field);
        var record = Record(new() { ["legal_form"] = "8888" });

        var selected = BlockingFields.Select(record, profile, (ProfileField f) => f.Roles.HasFlag(FieldRole.Identifier));

        Assert.Empty(selected);
    }

    [Fact]
    public void FieldPredicateOverload_NoNullEquivalentsDeclared_BehavesLikeBlankOnly()
    {
        // Absent nullEquivalents changes nothing: only an actually-blank value is excluded.
        var field = Field("legal_form", SemanticFieldType.LegalForm) with { Roles = FieldRole.Blocking | FieldRole.Identifier };
        var profile = Profile(field);
        var record = Record(new() { ["legal_form"] = "8888" });

        var selected = BlockingFields.Select(record, profile, (ProfileField f) => f.Roles.HasFlag(FieldRole.Identifier));

        Assert.Equal([("legal_form", "8888")], selected);
    }

    [Fact]
    public void BlankValue_StillExcluded_WithNullEquivalentsDeclared()
    {
        var field = Field("legal_form", SemanticFieldType.LegalForm, ["8888"]);
        var profile = Profile(field);
        var record = Record(new() { ["legal_form"] = "   " });

        var selected = BlockingFields.Select(record, profile, (SemanticFieldType t) => t == SemanticFieldType.LegalForm);

        Assert.Empty(selected);
    }
}
