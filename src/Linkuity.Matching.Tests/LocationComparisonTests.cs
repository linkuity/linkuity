using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Profiles.Configuration;
using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

/// <summary>
/// Street, postcode, city, region and country are nested facets of one fact. Scored as five
/// independent fields they contribute five times: on the GLEIF profile they summed to 11.4 bits
/// against a 13.3 bar, so an identical address plus a 40%-similar name auto-merged, while a
/// differing address vetoed an otherwise identical record.
///
/// A comparison asks one question with ordered answers and contributes exactly one of them.
/// These tests pin that: the ladder resolves first-match-wins, member fields produce no signal of
/// their own, and the score moves by one level's bits rather than by a sum.
///
/// The m and u values below are fixtures chosen to give exact powers of two, NOT calibrated
/// measurements — calibration is a separate step against a corpus.
/// </summary>
public class LocationComparisonTests
{
    private static IStrategyRegistry Registry() => MatchingDefaults.CreateRegistry();

    // street: log2(0.4/0.025)  =  4.0, capped to 3.0
    // postcode: log2(0.2/0.05) =  2.0
    // city: log2(0.2/0.1)      =  1.0
    // region: log2(0.1/0.2)    = -1.0
    // none: log2(0.1/0.4)      = -2.0   <- m < u, which FieldEvidence forbids and a level requires
    private const string Json = """
    {
      "contentType": "organization",
      "fields": [
        { "name": "organization_name", "semanticType": "OrganizationName", "roles": ["Searchable","Matchable"], "similarityEvaluator": "canonical-jaccard",
          "evidence": { "sameEntityAgreement": 0.6, "chanceAgreement": 0.006, "maxAgreementBits": 6.0 } },
        { "name": "address_line", "semanticType": "AddressLine", "roles": ["Matchable"], "similarityEvaluator": "jaccard" },
        { "name": "postal_code",  "semanticType": "PostalCode",  "roles": ["Matchable"], "similarityEvaluator": "exact" },
        { "name": "city",         "semanticType": "City",        "roles": ["Matchable"], "similarityEvaluator": "exact" },
        { "name": "region",       "semanticType": "Region",      "roles": ["Matchable"], "similarityEvaluator": "exact" }
      ],
      "comparisons": [
        {
          "name": "location",
          "fields": ["address_line", "postal_code", "city", "region"],
          "levels": [
            { "name": "same-street",   "requirements": [ { "field": "address_line", "minSimilarity": 0.9 }, { "field": "postal_code" } ],
              "evidence": { "sameEntityRate": 0.4, "chanceRate": 0.025, "maxBits": 3.0 } },
            { "name": "same-postcode", "requirements": [ { "field": "postal_code" } ],
              "evidence": { "sameEntityRate": 0.2, "chanceRate": 0.05, "maxBits": 3.0 } },
            { "name": "same-city",     "requirements": [ { "field": "city" } ],
              "evidence": { "sameEntityRate": 0.2, "chanceRate": 0.1, "maxBits": 3.0 } },
            { "name": "same-region",   "requirements": [ { "field": "region" } ],
              "evidence": { "sameEntityRate": 0.1, "chanceRate": 0.2 } },
            { "name": "none",          "requirements": [],
              "evidence": { "sameEntityRate": 0.1, "chanceRate": 0.4 } }
          ]
        }
      ],
      "normalizationStrategy": "identity",
      "blockingStrategies": ["exact-value"],
      "candidateRetrievalStrategy": "linear",
      "similarityStrategy": "field-weighted",
      "scoringStrategy": "evidence",
      "decisionStrategy": "threshold",
      "clusteringStrategy": "union-find",
      "autoMatchThreshold": 8.0,
      "reviewThreshold": 5.0
    }
    """;

    private static MatchingProfile Profile(string json = Json) =>
        new MatchingProfileConfigLoader().LoadFromJson(json, Registry());

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

    private static SimilaritySignal Location(MatchingProfile profile, EntityRecord left, EntityRecord right)
        => Registry().Similarity[profile.SimilarityStrategy].Evaluate(left, right, profile)
            .Single(s => s.Name == "location");

    private static double Score(MatchingProfile profile, EntityRecord left, EntityRecord right)
        => new EvidenceScoringStrategy()
            .Score(Registry().Similarity[profile.SimilarityStrategy].Evaluate(left, right, profile), profile)
            .FinalScore;

    // ---- LevelEvidence -----------------------------------------------------------------

    [Fact]
    public void LevelEvidence_BelowChance_ScoresNegative()
    {
        // The property FieldEvidence cannot express: it rejects m <= u outright, correctly for a
        // whole field, which is why levels needed their own evidence type.
        var evidence = new LevelEvidence { SameEntityRate = 0.1, ChanceRate = 0.4 };
        Assert.Equal(-2.0, evidence.Bits, 10);
    }

    [Fact]
    public void LevelEvidence_CapAppliesToPositiveBitsOnly()
    {
        Assert.Equal(3.0, new LevelEvidence { SameEntityRate = 0.4, ChanceRate = 0.025, MaxBits = 3.0 }.Bits, 10);
        Assert.Equal(-2.0, new LevelEvidence { SameEntityRate = 0.1, ChanceRate = 0.4, MaxBits = 3.0 }.Bits, 10);
    }

    [Theory]
    [InlineData(0.0, 0.5)]
    [InlineData(1.0, 0.5)]
    [InlineData(0.5, 0.0)]
    [InlineData(0.5, 1.0)]
    public void LevelEvidence_RatesOutsideTheOpenUnitInterval_Throw(double m, double u)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new LevelEvidence { SameEntityRate = m, ChanceRate = u });

    // ---- the ladder --------------------------------------------------------------------

    [Fact]
    public void FirstMatchWins_MostSpecificLevel()
    {
        var profile = Profile();
        // This pair satisfies every level. It must be priced as same-street, not as four levels.
        var signal = Location(profile,
            Record(("address_line", "245 SUMMER STREET"), ("postal_code", "02210"), ("city", "BOSTON"), ("region", "US-MA")),
            Record(("address_line", "245 SUMMER STREET"), ("postal_code", "02210"), ("city", "BOSTON"), ("region", "US-MA")));

        Assert.Equal("same-street", signal.Level);
        Assert.Equal(ComparisonOutcome.Compared, signal.Outcome);
    }

    [Theory]
    // street differs, postcode agrees -> falls to same-postcode
    [InlineData("1 OTHER ROAD", "02210", "BOSTON", "US-MA", "same-postcode")]
    // postcode differs too, city agrees -> same-city
    [InlineData("1 OTHER ROAD", "99999", "BOSTON", "US-MA", "same-city")]
    // city differs, region agrees -> same-region
    [InlineData("1 OTHER ROAD", "99999", "SALEM", "US-MA", "same-region")]
    // nothing agrees -> the catch-all
    [InlineData("1 OTHER ROAD", "99999", "SALEM", "US-NY", "none")]
    public void LadderDescends(string street, string postcode, string city, string region, string expected)
    {
        var profile = Profile();
        var signal = Location(profile,
            Record(("address_line", "245 SUMMER STREET"), ("postal_code", "02210"), ("city", "BOSTON"), ("region", "US-MA")),
            Record(("address_line", street), ("postal_code", postcode), ("city", city), ("region", region)));

        Assert.Equal(expected, signal.Level);
    }

    [Fact]
    public void GradedRequirement_IsAThresholdNotEquality()
    {
        // same-street needs address_line >= 0.9, so a near-identical street still reaches it while
        // postcode agrees. Token-set jaccard of the two below is 1.0 (SUITE ordering aside).
        var profile = Profile();
        var signal = Location(profile,
            Record(("address_line", "245 SUMMER STREET"), ("postal_code", "02210")),
            Record(("address_line", "SUMMER STREET 245"), ("postal_code", "02210")));

        Assert.Equal("same-street", signal.Level);
    }

    [Fact]
    public void MemberFields_ProduceNoSignalOfTheirOwn()
    {
        var profile = Profile();
        var signals = Registry().Similarity[profile.SimilarityStrategy].Evaluate(
            Record(("organization_name", "ACME"), ("city", "BOSTON")),
            Record(("organization_name", "ACME"), ("city", "BOSTON")),
            profile);

        Assert.Equal(["organization_name", "location"], signals.Select(s => s.Name).ToArray());
    }

    [Fact]
    public void NothingComparableOnBothSides_IsMissingNotDisagreement()
    {
        // A pair cannot disagree on an address neither record carries. Walking the ladder anyway
        // would land it in "none" and charge it -2 bits for data that was never there.
        var profile = Profile();

        var neither = Location(profile, Record(("organization_name", "ACME")), Record(("organization_name", "ACME")));
        Assert.Equal(ComparisonOutcome.MissingBoth, neither.Outcome);
        Assert.Null(neither.Level);

        var oneSide = Location(profile, Record(("city", "BOSTON")), Record(("organization_name", "ACME")));
        Assert.Equal(ComparisonOutcome.MissingOneSide, oneSide.Outcome);

        // Both sides carry location data, but never the SAME field, so nothing was compared.
        var disjoint = Location(profile, Record(("city", "BOSTON")), Record(("postal_code", "02210")));
        Assert.Equal(ComparisonOutcome.MissingOneSide, disjoint.Outcome);
        Assert.Null(disjoint.Level);
    }

    [Fact]
    public void SentinelMemberValue_DoesNotSatisfyARequirement()
    {
        var json = Json.Replace(
            "\"name\": \"city\",         \"semanticType\": \"City\",        \"roles\": [\"Matchable\"], \"similarityEvaluator\": \"exact\"",
            "\"name\": \"city\",         \"semanticType\": \"City\",        \"roles\": [\"Matchable\"], \"similarityEvaluator\": \"exact\", \"nullEquivalents\": [\"UNKNOWN\"]");
        Assert.NotEqual(Json, json);

        var profile = Profile(json);
        var signal = Location(profile,
            Record(("city", "UNKNOWN"), ("region", "US-MA")),
            Record(("city", "UNKNOWN"), ("region", "US-MA")));

        Assert.Equal("same-region", signal.Level);   // NOT same-city
    }

    // ---- scoring -----------------------------------------------------------------------

    [Theory]
    [InlineData("245 SUMMER STREET", "02210", "BOSTON", "US-MA", 3.0)]    // same-street, capped
    [InlineData("1 OTHER ROAD", "02210", "BOSTON", "US-MA", 2.0)]        // same-postcode
    [InlineData("1 OTHER ROAD", "99999", "BOSTON", "US-MA", 1.0)]        // same-city
    [InlineData("1 OTHER ROAD", "99999", "SALEM", "US-MA", -1.0)]        // same-region
    [InlineData("1 OTHER ROAD", "99999", "SALEM", "US-NY", -2.0)]        // none
    public void LocationContributesExactlyOneLevel(
        string street, string postcode, string city, string region, double expected)
    {
        var profile = Profile();
        var left = Record(("address_line", "245 SUMMER STREET"), ("postal_code", "02210"), ("city", "BOSTON"), ("region", "US-MA"));
        var right = Record(("address_line", street), ("postal_code", postcode), ("city", city), ("region", region));

        // No name on either side, so the total is the location contribution alone.
        Assert.Equal(expected, Score(profile, left, right), 10);
    }

    [Fact]
    public void FullAgreementOnEveryFacet_ScoresOneLevel_NotTheirSum()
    {
        // The defect this exists to fix: independently scored, agreeing on all four facets would
        // contribute 3.0 + 2.0 + 1.0 = 6.0 or more. One fact, one contribution.
        var profile = Profile();
        var identical = new (string, string)[]
        {
            ("address_line", "245 SUMMER STREET"), ("postal_code", "02210"), ("city", "BOSTON"), ("region", "US-MA")
        };

        Assert.Equal(3.0, Score(profile, Record(identical), Record(identical)), 10);
    }

    [Fact]
    public void MissingLocation_ContributesZero()
    {
        var profile = Profile();
        var score = Score(profile, Record(("organization_name", "ACME")), Record(("organization_name", "ACME")));

        // Name alone: log2(0.6/0.006) = 6.64, capped at 6.0. Location adds nothing either way.
        Assert.Equal(6.0, score, 10);
    }

    [Fact]
    public void Breakdown_NamesTheLevelAndCountsItOnce()
    {
        var profile = Profile();
        var signals = Registry().Similarity[profile.SimilarityStrategy].Evaluate(
            Record(("postal_code", "02210")), Record(("postal_code", "02210")), profile);

        var contribution = Assert.Single(
            new EvidenceScoringStrategy().Score(signals, profile).Breakdown,
            c => c.Signal == "location");

        Assert.Equal(2.0, contribution.Contribution, 10);
        Assert.Equal(3.0, contribution.Weight, 10);   // the best the ladder can award
    }

    // ---- loading -----------------------------------------------------------------------

    [Fact]
    public void Load_MapsTheLadderInDeclaredOrder()
    {
        var comparison = Assert.Single(Profile().Comparisons);
        Assert.Equal("location", comparison.Name);
        Assert.Equal(["same-street", "same-postcode", "same-city", "same-region", "none"],
                     comparison.Levels.Select(l => l.Name).ToArray());
        Assert.Equal(0.9, comparison.Levels[0].Requirements.Single(r => r.Field == "address_line").MinSimilarity);
        Assert.Equal(1.0, comparison.Levels[1].Requirements.Single().MinSimilarity);   // defaulted
    }

    [Theory]
    [InlineData("\"scoringStrategy\": \"evidence\"", "\"scoringStrategy\": \"identifier-weighted\"",
                "only the 'evidence' scorer")]
    [InlineData("{ \"name\": \"none\",          \"requirements\": [],",
                "{ \"name\": \"none\",          \"requirements\": [ { \"field\": \"city\" } ],",
                "last level must have none")]
    [InlineData("\"name\": \"same-city\",     \"requirements\": [ { \"field\": \"city\" } ]",
                "\"name\": \"same-city\",     \"requirements\": []",
                "every level below it is unreachable")]
    [InlineData("{ \"field\": \"city\" } ]", "{ \"field\": \"organization_name\" } ]",
                "not among the comparison's fields")]
    [InlineData("\"name\": \"location\",", "\"name\": \"city\",", "same name as a field")]
    [InlineData("\"fields\": [\"address_line\", \"postal_code\", \"city\", \"region\"]",
                "\"fields\": [\"address_line\", \"postal_code\", \"city\", \"nope\"]",
                "which the profile does not declare")]
    [InlineData("\"sameEntityRate\": 0.2, \"chanceRate\": 0.05, \"maxBits\": 3.0",
                "\"sameEntityRate\": 0.2, \"chanceRate\": 0.05",
                "must declare a ceiling")]
    [InlineData("\"name\": \"same-region\",", "\"name\": \"same-city\",", "more than once")]
    public void Load_InvalidComparison_Throws(string find, string replace, string expected)
    {
        var json = Json.Replace(find, replace);
        Assert.NotEqual(Json, json);   // guards the test against a stale find string

        var ex = Assert.Throws<MatchingProfileConfigException>(() => Profile(json));
        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_FieldInBothAComparisonAndAnAliasGroup_Throws()
    {
        var json = Json.Replace(
            "\"name\": \"city\",         \"semanticType\": \"City\",        \"roles\": [\"Matchable\"], \"similarityEvaluator\": \"exact\"",
            "\"name\": \"city\",         \"semanticType\": \"City\",        \"roles\": [\"Matchable\"], \"similarityEvaluator\": \"exact\", \"aliasGroup\": \"place\"");
        Assert.NotEqual(Json, json);

        var ex = Assert.Throws<MatchingProfileConfigException>(() => Profile(json));
        Assert.Contains("two mechanisms", ex.Message);
    }

    [Fact]
    public void Load_MemberFieldNeedsNoFieldEvidence()
    {
        // Member fields are never priced individually, so requiring m/u on them would be asking
        // for numbers nothing reads. None of the four location fields declares evidence.
        var profile = Profile();
        foreach (var name in new[] { "address_line", "postal_code", "city", "region" })
            Assert.Null(profile.Fields.Single(f => f.Name == name).Evidence);
    }

    // ---- fingerprint -------------------------------------------------------------------

    [Fact]
    public void Fingerprint_LevelOrderIsSemantic()
    {
        // First-match-wins, so swapping two rungs is a different rule and must not fingerprint
        // alike — unlike field order, which the fingerprint deliberately sorts away.
        var profile = Profile();
        var comparison = profile.Comparisons[0];
        var swapped = profile with
        {
            Comparisons = [comparison with
            {
                Levels = [comparison.Levels[1], comparison.Levels[0], .. comparison.Levels.Skip(2)]
            }]
        };

        Assert.NotEqual(ProfileFingerprint.Of(profile), ProfileFingerprint.Of(swapped));
    }

    [Fact]
    public void Fingerprint_DeclaringAComparisonChangesIt()
    {
        var profile = Profile();
        Assert.NotEqual(ProfileFingerprint.Of(profile), ProfileFingerprint.Of(profile with { Comparisons = [] }));
    }
}
