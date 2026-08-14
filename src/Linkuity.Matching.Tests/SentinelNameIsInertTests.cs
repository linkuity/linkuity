using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Profiles.Configuration;
using Linkuity.Matching.Strategies;

namespace Linkuity.Matching.Tests;

/// <summary>
/// A sentinel in a free-text NAME column is the worst case for a matcher, and the one the GLEIF
/// corpus actually contains: 478 distinct legal entities whose name is recorded as
/// 旧名称確認中 ("former name under confirmation"). Read as a real name it is both the single
/// largest false-agreement source AND — because blocking keys are built from values — a key that
/// collapses all 478 into one block before scoring ever gets a say.
///
/// The engine already has the mechanism (<see cref="ProfileField.NullEquivalents"/>). These tests
/// pin that it holds across the WHOLE strategy set rather than in the strategies someone
/// remembered, and they enumerate the registry so a blocking strategy added later is covered
/// without anyone remembering to come back here.
/// </summary>
public class SentinelNameIsInertTests
{
    private const string Sentinel = "旧名称確認中";
    private const string RealName = "Fidelity Advisor Leveraged Company Stock Fund";

    private static IStrategyRegistry Registry() => MatchingDefaults.CreateRegistry();

    private const string Json = """
    {
      "contentType": "organization",
      "fields": [
        { "name": "organization_name", "semanticType": "OrganizationName", "roles": ["Searchable","Matchable","Blocking"], "similarityEvaluator": "canonical-jaccard", "nullEquivalents": ["旧名称確認中"] },
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

    private static MatchingProfile Profile() =>
        new MatchingProfileConfigLoader().LoadFromJson(Json, Registry());

    private static EntityRecord Record(string name) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = Guid.Empty,
        SourceId = Guid.Empty,
        IngestBatchId = Guid.Empty,
        SourceRecordId = "r",
        Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["organization_name"] = name },
        CreatedAt = DateTimeOffset.UnixEpoch
    };

    [Fact]
    public void NoRegisteredBlockingStrategy_KeysASentinelName()
    {
        var profile = Profile();
        var sentinel = Record(Sentinel);
        var real = Record(RealName);

        var producedForRealName = new List<string>();

        foreach (var (name, strategy) in Registry().Blocking)
        {
            Assert.Empty(strategy.GenerateKeys(sentinel, profile));

            if (strategy.GenerateKeys(real, profile).Count > 0)
                producedForRealName.Add(name);
        }

        // Without this the test would pass just as happily if nothing keyed anything at all.
        Assert.NotEmpty(producedForRealName);
    }

    [Theory]
    [InlineData("旧名称確認中")]
    [InlineData("  旧名称確認中  ")]
    public void SentinelIsRecognisedRegardlessOfSurroundingWhitespace(string written)
        => Assert.True(Profile().Fields.Single(f => f.Name == "organization_name").IsAbsent(written));

    [Fact]
    public void TwoSentinelNames_CompareAsMissing_NotAsAgreement()
    {
        var profile = Profile();
        var similarity = Registry().Similarity[profile.SimilarityStrategy];

        var signal = similarity
            .Evaluate(Record(Sentinel), Record(Sentinel), profile)
            .Single(s => s.Name == "organization_name");

        Assert.Equal(ComparisonOutcome.MissingBoth, signal.Outcome);
        Assert.Equal(0, signal.Value);
    }

    [Fact]
    public void SentinelOnOneSide_IsMissingOneSide()
    {
        var profile = Profile();
        var similarity = Registry().Similarity[profile.SimilarityStrategy];

        var signal = similarity
            .Evaluate(Record(Sentinel), Record(RealName), profile)
            .Single(s => s.Name == "organization_name");

        Assert.Equal(ComparisonOutcome.MissingOneSide, signal.Outcome);
    }

    [Fact]
    public void RealNames_StillCompare()
    {
        // The control for the two tests above: absence handling must not be swallowing everything.
        var profile = Profile();
        var similarity = Registry().Similarity[profile.SimilarityStrategy];

        var signal = similarity
            .Evaluate(Record(RealName), Record(RealName), profile)
            .Single(s => s.Name == "organization_name");

        Assert.Equal(ComparisonOutcome.Compared, signal.Outcome);
        Assert.Equal(1.0, signal.Value, 10);
    }

    [Fact]
    public void SentinelName_DerivesNoLegalForm()
    {
        // A field derived from a sentinel must not carry a value either, or suppressing the name
        // would simply move the false agreement one field along.
        var profile = Profile();
        var normalized = ProfileNormalization.Resolve(Registry(), profile).Normalize(Record(Sentinel), profile);

        Assert.Equal("", normalized.Fields["legal_form_name"]);
    }

    [Fact]
    public void SentinelDeclaration_ChangesTheFingerprint()
    {
        // Provenance: two runs that treated 旧名称確認中 differently must not claim the same rules.
        var declared = Profile();
        var undeclared = declared with
        {
            Fields = declared.Fields
                .Select(f => f.NullEquivalents is null ? f : f with { NullEquivalents = null })
                .ToList()
        };

        Assert.NotEqual(ProfileFingerprint.Of(declared), ProfileFingerprint.Of(undeclared));
    }
}
