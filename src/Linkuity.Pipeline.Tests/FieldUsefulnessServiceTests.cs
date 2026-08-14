using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Profiles.Configuration;

namespace Linkuity.Pipeline.Tests;

/// <summary>
/// The report that ends the guess-adjust-repeat loop: which columns are actually worth matching
/// on, and what this data can never resolve.
///
/// The trap it exists to expose is a field the same entity agrees on almost always — which looks
/// excellent — where unrelated records agree nearly as often. Country is the canonical case: 99%
/// against 87% is close to no information, and only seeing both numbers together says so.
/// </summary>
public class FieldUsefulnessServiceTests
{
    private const string Json = """
    {
      "contentType": "organization",
      "fields": [
        { "name": "email",   "semanticType": "Email",           "roles": ["Matchable","Blocking"], "similarityEvaluator": "exact" },
        { "name": "name",    "semanticType": "OrganizationName","roles": ["Matchable","Blocking"], "similarityEvaluator": "exact" },
        { "name": "country", "semanticType": "Country",         "roles": ["Matchable"],            "similarityEvaluator": "exact",
          "nullEquivalents": ["UNKNOWN"] }
      ],
      "normalizationStrategy": "identity",
      "blockingStrategies": ["exact-value","token","fingerprint"],
      "candidateRetrievalStrategy": "linear",
      "similarityStrategy": "field-weighted",
      "scoringStrategy": "identifier-weighted",
      "decisionStrategy": "threshold",
      "clusteringStrategy": "union-find",
      "autoMatchThreshold": 0.9,
      "reviewThreshold": 0.5
    }
    """;

    private static MatchingProfile Profile() =>
        new MatchingProfileConfigLoader().LoadFromJson(Json, MatchingDefaults.CreateRegistry());

    private static EntityRecord Record(string id, string email, string name, string country) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = Guid.Empty,
        SourceId = Guid.Empty,
        IngestBatchId = Guid.Empty,
        SourceRecordId = id,
        Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["email"] = email, ["name"] = name, ["country"] = country },
        CreatedAt = DateTimeOffset.UnixEpoch
    };

    private static FieldUsefulnessResult Analyze(
        IReadOnlyList<EntityRecord> records, IReadOnlyDictionary<string, string> truth)
        => new FieldUsefulnessService(MatchingDefaults.CreateRegistry())
            .Analyze(records, Profile(), truth);

    /// <summary>
    /// Four entities of two records each, arranged so that DIFFERENT entities are actually
    /// compared — without that there are no cross-entity candidate pairs and chance agreement
    /// cannot be measured at all.
    /// <para>
    /// E1/E2 share the name "ACME" and E3/E4 share "BETA", so name-blocking puts records of
    /// different companies in front of the comparer. Email is unique per entity. Everyone is in
    /// GB, so country agrees for same and different entities alike — the exact shape the report
    /// has to expose as worthless.
    /// </para>
    /// </summary>
    private static (List<EntityRecord> Records, Dictionary<string, string> Truth) Corpus()
    {
        var records = new List<EntityRecord>();
        var truth = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new[] { "ACME", "ACME", "BETA", "BETA" };

        foreach (var i in Enumerable.Range(1, 4))
        {
            records.Add(Record($"a{i}", $"e{i}@x.test", names[i - 1], "GB"));
            records.Add(Record($"b{i}", $"e{i}@x.test", names[i - 1], "GB"));
            truth[$"a{i}"] = $"E{i}";
            truth[$"b{i}"] = $"E{i}";
        }
        return (records, truth);
    }

    [Fact]
    public void ReportsOneRowPerMatchableField()
    {
        var (records, truth) = Corpus();

        var result = Analyze(records, truth);

        Assert.Equal(["email", "name", "country"], result.Fields.Select(f => f.FieldName).ToArray());
        Assert.Equal(8, result.Records);
        Assert.Equal(8, result.LabeledRecords);
    }

    [Fact]
    public void FillRate_TreatsASentinelAsUnfilled()
    {
        // A column full of "UNKNOWN" is not a filled column. Reporting it as 100% populated would
        // recommend matching on nothing.
        var (records, truth) = Corpus();
        records.Add(Record("z1", "z@x.test", "ZED", "UNKNOWN"));
        truth["z1"] = "EZ";

        var country = Analyze(records, truth).Fields.Single(f => f.FieldName == "country");

        Assert.Equal(8, country.RecordsFilled);              // the UNKNOWN row does not count
        Assert.Equal(8.0 / 9.0, country.FillRate, 10);
    }

    [Fact]
    public void AFieldEveryoneAgreesOn_IsReportedAsNearlyUseless()
    {
        // Country agrees for same entities AND for different ones, so it separates nothing. This
        // is the whole point of showing both rates side by side.
        var (records, truth) = Corpus();

        var country = Analyze(records, truth).Fields.Single(f => f.FieldName == "country");

        Assert.NotNull(country.Bits);
        Assert.True(country.Bits < 0.5,
            $"country should carry almost no evidence, measured {country.Bits} bits");
        Assert.Equal("nearly useless", country.Verdict);
    }

    [Fact]
    public void ADiscriminatingField_OutranksAGenericOne()
    {
        // Not asserting an absolute figure: the point is the ORDERING the report exists to reveal.
        var fields = Analyze(Corpus().Records, Corpus().Truth);
        var email = fields.Fields.Single(f => f.FieldName == "email");
        var country = fields.Fields.Single(f => f.FieldName == "country");

        Assert.True(email.Bits > country.Bits,
            $"email ({email.Bits}) should outrank country ({country.Bits})");
    }

    [Theory]
    [InlineData(null, "not measured")]
    [InlineData(7.0, "very strong")]
    [InlineData(4.0, "strong")]
    [InlineData(2.0, "moderate")]
    [InlineData(1.0, "weak")]
    [InlineData(0.2, "nearly useless")]
    public void VerdictBands_ReadTheEvidence(double? bits, string expected)
    {
        // Constructed from rates that produce the wanted bits, so the band is exercised through
        // the same path a real row takes.
        var row = bits is null
            ? new FieldUsefulnessRow("f", 1.0, 1, null, null, 0, 0)
            : new FieldUsefulnessRow("f", 1.0, 1, 0.8, 0.8 / Math.Pow(2, bits.Value), 10, 10);

        Assert.Equal(expected, row.Verdict);
    }

    [Fact]
    public void UnmeasurableField_IsNotMeasured_RatherThanZero()
    {
        // "We could not tell" and "this is worthless" are different answers, and collapsing them
        // would retire a field nobody actually measured.
        var row = new FieldUsefulnessRow("f", 1.0, 1, null, null, 0, 0);

        Assert.Null(row.Bits);
        Assert.Equal("not measured", row.Verdict);
    }

    // ---- what the data cannot resolve ----

    [Fact]
    public void DistinguishableCorpus_ReportsNoUnresolvableGroups()
    {
        var (records, truth) = Corpus();

        Assert.Equal(0, Analyze(records, truth).Indistinguishable.Groups);
    }

    [Fact]
    public void IdenticalRecordsOfDifferentEntities_AreReportedAsUnresolvable()
    {
        // The fund-share-class case: same name, same country, no email on either, different
        // companies. No threshold separates these.
        var (records, truth) = Corpus();
        records.Add(Record("f1", "", "GLOBAL BOND FUND", "GB"));
        records.Add(Record("f2", "", "GLOBAL BOND FUND", "GB"));
        records.Add(Record("f3", "", "GLOBAL BOND FUND", "GB"));
        truth["f1"] = "F1";
        truth["f2"] = "F2";
        truth["f3"] = "F3";

        var ind = Analyze(records, truth).Indistinguishable;

        Assert.Equal(1, ind.Groups);
        Assert.Equal(3, ind.RecordsInvolved);
        Assert.Equal(3, ind.LargestGroupRecords);
        Assert.Equal(3, ind.LargestGroupEntities);
        Assert.Equal(["email"], ind.LargestGroupUnfilledFields);   // what to collect to separate them
    }

    [Fact]
    public void IdenticalRecordsOfTheSameEntity_AreNotUnresolvable()
    {
        // Duplicates of ONE company being identical is the system working, not a limitation.
        var records = new List<EntityRecord>
        {
            Record("s1", "", "SAME CO", "GB"),
            Record("s2", "", "SAME CO", "GB")
        };
        var truth = new Dictionary<string, string>(StringComparer.Ordinal) { ["s1"] = "S", ["s2"] = "S" };

        Assert.Equal(0, Analyze(records, truth).Indistinguishable.Groups);
    }

    [Fact]
    public void UnlabeledRecords_CannotCreateAnUnresolvableGroup()
    {
        // An unlabeled record cannot be shown to be a DIFFERENT entity from anything, so counting
        // it would report merely-unverified records as impossible ones.
        var records = new List<EntityRecord>
        {
            Record("u1", "", "MYSTERY CO", "GB"),
            Record("u2", "", "MYSTERY CO", "GB")
        };

        var result = Analyze(records, new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Equal(0, result.Indistinguishable.Groups);
        Assert.Equal(0, result.LabeledRecords);
    }
}
