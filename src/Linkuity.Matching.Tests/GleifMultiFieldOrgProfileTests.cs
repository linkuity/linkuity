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
/// <c>C:\dev\datasets\gleif-org-two-observation.multifield.profile.json</c>, the same convention
/// <c>gleif-org.profile.json</c> and <c>sec-recall.evidence.profile.json</c> already follow — the
/// corpus it is calibrated against is 591 MB and is not in version control either, so a copy under
/// <c>docs/</c> would be a file nothing could regenerate or check. <see cref="ProfileJson"/> is a
/// PINNED COPY of that file, kept here so the invariants below are enforced by CI rather than by
/// remembering them. <see cref="PinnedCopy_MatchesTheShippedFile_WhenItIsPresent"/> fails loudly if
/// the two drift apart on a machine that actually holds the artifact.
/// </para>
/// <para>
/// The artifact is calibrated against the TWO-OBSERVATION corpus (a record is one name paired with
/// one address), NOT the alias-only <c>gleif-org</c> corpus. That distinction is the reason this
/// file exists in its current form: under the alias-only corpus every non-name column was copied
/// onto every alias row, so m was 1.0 by construction for seven of eight fields and
/// <c>log2((1-m)/(1-u))</c> was set by the smoothing constant rather than measured. The numbers
/// below are measured on both sides for all six scored fields.
/// </para>
/// <para>
/// These are not restatements of loader validation. The loader rejects a null cap or a cap at/above
/// the auto threshold on ANY profile; what it cannot know is that none of these GLEIF columns is
/// unique to one entity (so none may claim the Identifier role), and that two of the eight are not
/// calibratable from this corpus at all — the judgements this file pins.
/// </para>
/// </summary>
public sealed class GleifMultiFieldOrgProfileTests
{
    private const string ShippedFilePath =
        @"C:\dev\datasets\gleif-org-two-observation.multifield.profile.json";

    /// <summary>The six fields the corpus can measure m for, i.e. the ones that vary within an
    /// entity. Order is the profile's own.</summary>
    private static readonly string[] ScoredFields =
        ["organization_name", "address_line", "city", "region", "postal_code", "country"];

    /// <summary>The two GLEIF records exactly once per ENTITY — never per address, never per alias
    /// — so nothing extractable from this snapshot makes them disagree within an entity.</summary>
    private static readonly string[] UncalibratableFields = ["jurisdiction", "legal_form"];

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
    /// The standing rule this redo exists to apply: A FIELD THE DATA CANNOT CALIBRATE DOES NOT
    /// RECEIVE AN INVENTED m/u. The two-observation corpus's acceptance gate measured
    /// within-entity variation of exactly 0.0000 % for both of these — GLEIF records one
    /// jurisdiction and one legal form per ENTITY, never per address, so there is no second
    /// independently observed value to disagree with even in principle. They stay in the profile so
    /// the decision is visible rather than silently omitted, but they are Searchable ONLY: no
    /// evidence (nothing to put in it), no Matchable (or
    /// <see cref="EvidenceScoringStrategy"/> would demand evidence and throw mid-run), and no
    /// Blocking. Written as an assertion so that promoting either one is a deliberate, reviewed
    /// edit that has to come with a calibration.
    /// </summary>
    [Fact]
    public void ShippedProfile_LeavesTheTwoUncalibratableFieldsSearchableOnlyAndWithoutEvidence()
    {
        var profile = Load();

        foreach (var name in UncalibratableFields)
        {
            var field = profile.Fields.Single(f => f.Name == name);
            Assert.Null(field.Evidence);
            Assert.Equal(FieldRole.Searchable, field.Roles);
        }

        Assert.Equal(ScoredFields,
            profile.Fields.Where(f => f.Roles.HasFlag(FieldRole.Matchable)).Select(f => f.Name));
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
    /// Only these two survive <c>maxBlockSize: 50</c>. Measured over all 4,492,972 gated records,
    /// country has 238 distinct values (US alone on 683,309), jurisdiction 307, legal_form 2,476 —
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
    /// sits on 480,057 of 4,492,972 gated records — 10.68 %. 9999 is deliberately NOT declared:
    /// GLEIF defines it as "entities which have no separate legal form", which is a positive fact
    /// those entities share, not an absence. The declaration is inert while legal_form is Searchable
    /// only — it exists so the judgement survives a future promotion to Matchable rather than having
    /// to be rediscovered then.
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
    /// adding one later is a deliberate, reviewed edit: the sentinel investigation over the
    /// two-observation corpus found no "unknown" code in country (all 238 values are valid
    /// ISO 3166-1 alpha-2 — note NA is Namibia, not "not applicable", on 162 records) and none in
    /// jurisdiction (XX/UN/EU mark supranational and treaty-based entities such as EUROCONTROL and
    /// the UN system, which is a real jurisdiction fact).
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
    /// evidence scorer, over a record pair carrying all eight columns. The pairing that matters here
    /// is the one that makes the Searchable-only declaration safe —
    /// <see cref="WeightedFieldSimilarityStrategy"/> emits a signal only for Matchable fields, so
    /// jurisdiction and legal_form never reach <see cref="EvidenceScoringStrategy"/>, which would
    /// otherwise throw on a field with no evidence. Six signals, not eight.
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

        Assert.Equal(ScoredFields, signals.Select(s => s.Name));
        Assert.True(double.IsFinite(result.FinalScore));
        Assert.DoesNotContain(result.Breakdown, c => UncalibratableFields.Contains(c.Signal));
    }

    /// <summary>
    /// The full-agreement score is exactly the sum of the caps, and it must stay strictly above the
    /// auto threshold — otherwise no pair could ever auto-merge. Pinned because the two move
    /// independently: a recalibration changes the caps, and a re-derived threshold changes the other
    /// side, and nothing else would notice if they crossed. The ratio is the number the calibration
    /// report tracks (auto is 74.0 % of the evidence a perfect pair can accumulate).
    /// </summary>
    [Fact]
    public void ShippedProfile_AutoThresholdIsReachableButNotByAnyOneField()
    {
        var profile = Load();
        var capsSum = profile.Fields
            .Where(f => f.Evidence is not null)
            .Sum(f => f.Evidence!.MaxAgreementBits!.Value);

        Assert.Equal(17.965, capsSum, 3);
        Assert.True(profile.AutoMatchThreshold < capsSum,
            $"autoMatchThreshold {profile.AutoMatchThreshold} must be below the caps sum {capsSum}");
        Assert.True(profile.ReviewThreshold < profile.AutoMatchThreshold);
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
    /// PINNED COPY of <c>C:\dev\datasets\gleif-org-two-observation.multifield.profile.json</c>.
    /// m and u are the measured Fellegi-Sunter parameters from `match corpus calibrate` over the fit
    /// half of the 4,492,972-record gated two-observation GLEIF corpus (2,246,729 fit records,
    /// 28,569,161 candidate pairs, 243,729 labelled same-entity pairs). Every raw rate lies strictly
    /// inside (0,1), so no bit here rests on the continuity constant. They are measurements, not
    /// tuning knobs, and must only change when a recalibration is reported. The thresholds come from
    /// a rule fixed before the sweep was run. Keep byte-identical to the artifact — see
    /// <see cref="PinnedCopy_MatchesTheShippedFile_WhenItIsPresent"/>.
    /// </summary>
    private const string ProfileJson = """
    {
      "contentType": "organization",
      "fields": [
        { "name": "organization_name", "semanticType": "OrganizationName", "roles": ["Searchable","Matchable","Blocking"], "similarityEvaluator": "canonical-jaccard", "weight": 4.0,
          "evidence": { "sameEntityAgreement": 0.595498, "chanceAgreement": 0.006417, "maxAgreementBits": 6.536 } },
        { "name": "address_line", "semanticType": "AddressLine", "roles": ["Searchable","Matchable"], "similarityEvaluator": "jaccard",
          "evidence": { "sameEntityAgreement": 0.406070, "chanceAgreement": 0.027075, "maxAgreementBits": 3.906 } },
        { "name": "city", "semanticType": "City", "roles": ["Searchable","Matchable"], "similarityEvaluator": "exact",
          "evidence": { "sameEntityAgreement": 0.686030, "chanceAgreement": 0.223787, "maxAgreementBits": 1.616 } },
        { "name": "region", "semanticType": "Region", "roles": ["Searchable","Matchable"], "similarityEvaluator": "exact",
          "evidence": { "sameEntityAgreement": 0.761669, "chanceAgreement": 0.330561, "maxAgreementBits": 1.204 } },
        { "name": "postal_code", "semanticType": "PostalCode", "roles": ["Searchable","Matchable","Blocking"], "similarityEvaluator": "exact",
          "evidence": { "sameEntityAgreement": 0.542499, "chanceAgreement": 0.037292, "maxAgreementBits": 3.862 } },
        { "name": "country", "semanticType": "Country", "roles": ["Searchable","Matchable"], "similarityEvaluator": "exact",
          "evidence": { "sameEntityAgreement": 0.938368, "chanceAgreement": 0.523514, "maxAgreementBits": 0.841 } },
        { "name": "jurisdiction", "semanticType": "Jurisdiction", "roles": ["Searchable"] },
        { "name": "legal_form", "semanticType": "LegalForm", "roles": ["Searchable"], "nullEquivalents": ["8888"] }
      ],
      "normalizationStrategy": "identity",
      "maxBlockSize": 50,
      "blockingStrategies": ["exact-value", "fingerprint", "phonetic", "token", "acronym", "ngram"],
      "candidateRetrievalStrategy": "linear",
      "similarityStrategy": "field-weighted",
      "scoringStrategy": "evidence",
      "decisionStrategy": "threshold",
      "clusteringStrategy": "union-find",
      "autoMatchThreshold": 13.297473,
      "reviewThreshold": 12.744498
    }
    """;
}
