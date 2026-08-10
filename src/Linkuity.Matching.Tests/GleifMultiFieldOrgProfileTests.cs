using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Profiles.Configuration;
using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

/// <summary>
/// Invariants of the shipped GLEIF multi-field organization evidence profile.
/// <para>
/// The profile itself is a measurement artifact and lives OUTSIDE the repo, at
/// <c>C:\dev\datasets\gleif-org.multifield.profile.json</c>, the same convention
/// <c>gleif-org.profile.json</c> and <c>sec-recall.evidence.profile.json</c> already follow — the
/// corpus it is calibrated against is 500 MB and is not in version control either, so a copy under
/// <c>docs/</c> would be a file nothing could regenerate or check. <see cref="ProfileJson"/> is a
/// PINNED COPY of that file, kept here so the invariants below are enforced by CI rather than by
/// remembering them. <see cref="PinnedCopy_MatchesTheShippedFile_WhenItIsPresent"/> fails loudly if
/// the two drift apart on a machine that actually holds the artifact.
/// </para>
/// <para>
/// These are not restatements of loader validation. The loader rejects a null cap or a cap at/above
/// the auto threshold on ANY profile; what it cannot know is that none of these eight GLEIF columns
/// is unique to one entity, so none of them may claim the Identifier role — the judgement this file
/// pins.
/// </para>
/// </summary>
public sealed class GleifMultiFieldOrgProfileTests
{
    private const string ShippedFilePath = @"C:\dev\datasets\gleif-org.multifield.profile.json";

    private static MatchingProfile Load()
        => new MatchingProfileConfigLoader().LoadFromJson(ProfileJson, MatchingDefaults.CreateRegistry());

    [Fact]
    public void ShippedProfile_Loads()
    {
        var profile = Load();

        Assert.Equal("organization", profile.ContentType);
        Assert.Equal("evidence", profile.ScoringStrategy);
        Assert.Equal("field-weighted", profile.SimilarityStrategy);
        Assert.Equal("identity", profile.NormalizationStrategy);
        Assert.Equal(50, profile.MaxBlockSize);
        Assert.Equal(8, profile.Fields.Count);
    }

    /// <summary>
    /// The constraint this task exists to hold. <see cref="FieldRole.Identifier"/> does two things
    /// neither of which is true of any GLEIF column here: it lets
    /// <see cref="IdentifierAwareWeightedScoringStrategy"/> floor a weakly-similar pair straight to
    /// the auto-merge band, and it is the ONLY role under which
    /// <see cref="MatchingProfileConfigLoader"/> permits a null (uncapped) agreement weight. A
    /// registered-agent postcode, a country, a legal form — every one of these is shared by
    /// thousands of unrelated companies, so an exact match on one is never decisive on its own.
    /// </summary>
    [Fact]
    public void ShippedProfile_DeclaresNoIdentifierField()
    {
        var identifiers = Load().Fields
            .Where(f => f.Roles.HasFlag(FieldRole.Identifier))
            .Select(f => f.Name)
            .ToList();

        Assert.Empty(identifiers);
    }

    /// <summary>
    /// Every Matchable field must carry evidence with an EXPLICIT, finite cap strictly below the
    /// auto threshold. The loader enforces the cap rules for fields that declare evidence at all;
    /// what it does not enforce is that every Matchable field declares it — that failure surfaces
    /// only at score time, as an <see cref="InvalidOperationException"/> from
    /// <see cref="EvidenceScoringStrategy"/>, i.e. mid-run rather than at load.
    /// </summary>
    [Fact]
    public void ShippedProfile_EveryMatchableFieldHasEvidenceWithAnExplicitCapBelowAuto()
    {
        var profile = Load();

        foreach (var field in profile.Fields.Where(f => f.Roles.HasFlag(FieldRole.Matchable)))
        {
            Assert.NotNull(field.Evidence);
            var cap = field.Evidence!.MaxAgreementBits;
            Assert.NotNull(cap);
            Assert.True(cap!.Value < profile.AutoMatchThreshold,
                $"field '{field.Name}' cap {cap.Value} must be below autoMatchThreshold {profile.AutoMatchThreshold}");
            // m > u, or agreeing on the field would be evidence AGAINST a match.
            Assert.True(field.Evidence.SameEntityAgreement > field.Evidence.ChanceAgreement, field.Name);
        }
    }

    /// <summary>
    /// The cap rule the calibration report pre-registered: each cap is the field's own measured
    /// log2(m/u), truncated down, so it is declarative and never binding. If a future edit lowers a
    /// cap below the measurement the field silently stops being worth what it was measured to be
    /// worth, and every score shifts — this catches that.
    /// </summary>
    [Fact]
    public void ShippedProfile_EveryCapEqualsItsOwnMeasuredAgreementBitsTruncated()
    {
        foreach (var field in Load().Fields.Where(f => f.Evidence is not null))
        {
            var e = field.Evidence!;
            var measured = Math.Log2(e.SameEntityAgreement / e.ChanceAgreement);
            var expected = Math.Floor(measured * 1000) / 1000;
            Assert.Equal(expected, e.MaxAgreementBits!.Value, 3);
            Assert.True(e.MaxAgreementBits.Value <= measured, field.Name);
        }
    }

    /// <summary>
    /// Only these two survive <c>maxBlockSize: 50</c>. Measured over all 3,946,009 gated records,
    /// country has 236 distinct values (IN alone on 460,923), jurisdiction 308, legal_form 2,476 —
    /// every meaningful key those three could emit is suppressed, so declaring them Blocking would
    /// cost key generation and buy no reachability.
    /// </summary>
    [Fact]
    public void ShippedProfile_BlocksOnOrganizationNameAndPostalCodeOnly()
    {
        var blocking = Load().Fields
            .Where(f => f.Roles.HasFlag(FieldRole.Blocking))
            .Select(f => f.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["organization_name", "postal_code"], blocking);
    }

    /// <summary>
    /// GLEIF's ELF code 8888 ("a legal form not yet on the ELF list; a new code has been requested")
    /// sits on 399,993 of 3,946,009 gated records — 10.14 %. Undeclared, two unrelated companies
    /// both carrying it would read as agreeing on legal form, and 400,000 records would collapse
    /// into one blocking key. 9999 is deliberately NOT declared: GLEIF defines it as "entities which
    /// have no separate legal form", which is a positive fact those entities share, not an absence.
    /// </summary>
    [Fact]
    public void ShippedProfile_TreatsTheLegalFormNotProvidedCodeAsAbsent()
    {
        var legalForm = Load().Fields.Single(f => f.Name == "legal_form");

        Assert.True(legalForm.IsAbsent("8888"));
        Assert.True(legalForm.IsAbsent("  8888 "));
        Assert.False(legalForm.IsAbsent("9999"));
        Assert.False(legalForm.IsAbsent("OV32"));
    }

    /// <summary>
    /// No other field declares a sentinel. Recorded as an assertion rather than an absence so that
    /// adding one later is a deliberate, reviewed edit: the sentinel investigation found no
    /// "unknown" code in country (all 236 values are valid ISO 3166-1 alpha-2 — note NA is Namibia,
    /// not "not applicable") and none in jurisdiction (XX/UN/EU mark supranational and treaty-based
    /// entities such as EUROCONTROL and the UN system, which is a real jurisdiction fact).
    /// </summary>
    [Fact]
    public void ShippedProfile_DeclaresNoSentinelOnAnyFieldButLegalForm()
    {
        var withSentinels = Load().Fields
            .Where(f => f.NullEquivalents is { Count: > 0 })
            .Select(f => f.Name)
            .ToList();

        Assert.Equal(["legal_form"], withSentinels);
    }

    /// <summary>
    /// End-to-end proof the profile is actually scorable: the real similarity strategy and the real
    /// evidence scorer, over a record pair carrying all eight columns. Without evidence on some
    /// Matchable field this throws rather than returning a number, and it would throw mid-run on the
    /// corpus rather than at load time.
    /// </summary>
    [Fact]
    public void ShippedProfile_ScoresAPairWithoutThrowing()
    {
        var profile = Load();
        var registry = MatchingDefaults.CreateRegistry();
        var similarity = registry.Similarity[profile.SimilarityStrategy];
        var scoring = registry.Scoring[profile.ScoringStrategy];

        var left = Record("r1", "ACME HOLDINGS LIMITED");
        var right = Record("r2", "ACME HOLDINGS LTD");

        var signals = similarity.Evaluate(left, right, profile);
        var result = scoring.Score(signals, profile);

        Assert.Equal(8, signals.Count);
        Assert.True(double.IsFinite(result.FinalScore));
        // Both sides carry the 8888 sentinel, so legal_form must contribute nothing at all rather
        // than its agreement bits.
        var legalForm = result.Breakdown.Single(c => c.Signal == "legal_form");
        Assert.Equal(ComparisonOutcome.MissingBoth, legalForm.Outcome);
        Assert.Equal(0, legalForm.Contribution);
    }

    /// <summary>
    /// Drift guard. On a machine that holds the measurement artifact, the pinned copy above must be
    /// the file the corpus runs actually use; otherwise this test suite would be certifying
    /// invariants of a profile nobody runs. Where the artifact is absent (CI, a clean checkout) the
    /// pinned copy is all there is and the other tests still hold it to the invariants.
    /// </summary>
    [Fact]
    public void PinnedCopy_MatchesTheShippedFile_WhenItIsPresent()
    {
        if (!File.Exists(ShippedFilePath))
            return;

        static string Canonical(string s) => s.Replace("\r\n", "\n").TrimEnd('\n');

        Assert.Equal(Canonical(ProfileJson), Canonical(File.ReadAllText(ShippedFilePath)));
    }

    private static EntityRecord Record(string id, string name) => new()
    {
        Id = Guid.NewGuid(), ProjectId = Guid.Empty, SourceId = Guid.Empty, IngestBatchId = Guid.Empty,
        SourceRecordId = id,
        CreatedAt = DateTimeOffset.UnixEpoch,
        Fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["organization_name"] = name,
            ["address_line"] = "245 SUMMER STREET",
            ["city"] = "BOSTON",
            ["region"] = "US-MA",
            ["postal_code"] = "02210",
            ["country"] = "US",
            ["jurisdiction"] = "US",
            ["legal_form"] = "8888"
        }
    };

    /// <summary>
    /// PINNED COPY of <c>C:\dev\datasets\gleif-org.multifield.profile.json</c>. m and u are the
    /// measured Fellegi-Sunter parameters from `match corpus calibrate` over the fit half of the
    /// 3,946,009-record gated GLEIF corpus (1,973,735 fit records, 26,158,095 candidate pairs);
    /// they are measurements, not tuning knobs, and must only change when a recalibration is
    /// reported. Keep byte-identical to the artifact — see
    /// <see cref="PinnedCopy_MatchesTheShippedFile_WhenItIsPresent"/>.
    /// </summary>
    private const string ProfileJson = """
    {
      "contentType": "organization",
      "fields": [
        { "name": "organization_name", "semanticType": "OrganizationName", "roles": ["Searchable","Matchable","Blocking"], "similarityEvaluator": "canonical-jaccard", "weight": 4.0,
          "evidence": { "sameEntityAgreement": 0.069936, "chanceAgreement": 0.005935, "maxAgreementBits": 3.558 } },
        { "name": "address_line", "semanticType": "AddressLine", "roles": ["Searchable","Matchable"], "similarityEvaluator": "jaccard",
          "evidence": { "sameEntityAgreement": 0.999995, "chanceAgreement": 0.030006, "maxAgreementBits": 5.058 } },
        { "name": "city", "semanticType": "City", "roles": ["Searchable","Matchable"], "similarityEvaluator": "exact",
          "evidence": { "sameEntityAgreement": 0.999995, "chanceAgreement": 0.225205, "maxAgreementBits": 2.150 } },
        { "name": "region", "semanticType": "Region", "roles": ["Searchable","Matchable"], "similarityEvaluator": "exact",
          "evidence": { "sameEntityAgreement": 0.999995, "chanceAgreement": 0.337534, "maxAgreementBits": 1.566 } },
        { "name": "postal_code", "semanticType": "PostalCode", "roles": ["Searchable","Matchable","Blocking"], "similarityEvaluator": "exact",
          "evidence": { "sameEntityAgreement": 0.999992, "chanceAgreement": 0.037068, "maxAgreementBits": 4.753 } },
        { "name": "country", "semanticType": "Country", "roles": ["Searchable","Matchable"], "similarityEvaluator": "exact",
          "evidence": { "sameEntityAgreement": 0.999995, "chanceAgreement": 0.518235, "maxAgreementBits": 0.948 } },
        { "name": "jurisdiction", "semanticType": "Jurisdiction", "roles": ["Searchable","Matchable"], "similarityEvaluator": "exact",
          "evidence": { "sameEntityAgreement": 0.999995, "chanceAgreement": 0.493788, "maxAgreementBits": 1.018 } },
        { "name": "legal_form", "semanticType": "LegalForm", "roles": ["Searchable","Matchable"], "similarityEvaluator": "exact",
          "nullEquivalents": ["8888"],
          "evidence": { "sameEntityAgreement": 0.999995, "chanceAgreement": 0.299057, "maxAgreementBits": 1.741 } }
      ],
      "normalizationStrategy": "identity",
      "maxBlockSize": 50,
      "blockingStrategies": ["exact-value", "fingerprint", "phonetic", "token", "acronym", "ngram"],
      "candidateRetrievalStrategy": "linear",
      "similarityStrategy": "field-weighted",
      "scoringStrategy": "evidence",
      "decisionStrategy": "threshold",
      "clusteringStrategy": "union-find",
      "autoMatchThreshold": 20.335249,
      "reviewThreshold": 17.764396
    }
    """;
}
