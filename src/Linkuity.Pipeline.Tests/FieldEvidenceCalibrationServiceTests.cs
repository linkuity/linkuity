using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;

namespace Linkuity.Pipeline.Tests;

/// <summary>
/// Field-evidence calibration: estimates m and u per matchable field from labelled candidate
/// pairs on the fit half of a deterministic hash-of-id split (see
/// <see cref="FieldEvidenceCalibrationService"/> class doc).
/// <para>
/// Fixture: four records, all carrying the same "block" value ("Smith") so a single "token-name"
/// blocking key puts all of them — and only them, from the FIT half — in one block of size 4,
/// which yields the 6 candidate pairs C(4,2). Two extra records (e1, e2) are added ONLY to prove
/// isolation: their record ids hash (SHA-256, see the constants below) to the EVAL half at
/// fitFraction 0.5, they are labelled a third true-pair group, and they deliberately DISAGREE on
/// field "a" — if the eval half leaked into the candidate walk, field "a"'s raw m would drop from
/// 1.0 to 2/3 and the boundary-smoothing assertions below would fail.
/// </para>
/// <para>
/// Ground truth: r5/r6 = group G1 (a true pair), r9/r10 = group G2 (a true pair), e1/e2 = group
/// G3 (eval-only, never candidates). Of the 6 fit-half candidate pairs, r5-r6 and r9-r10 are
/// same-entity (2 total); the other four (r5-r9, r5-r10, r6-r9, r6-r10) are different-entity, ALL
/// owned by the single "name:smith" key ("block"'s own key — the only blocking field here).
/// </para>
/// <para>
/// Field "a" (exact): r5.a=r6.a="X", r9.a=r10.a="Y" — every same-entity pair agrees (raw m = 2/2
/// = 1.0, the boundary FieldEvidence refuses). The "block" origin's determination on "a": among
/// its 4 different-entity observations, 0 agree (rate 0.0) — far below threshold, so NOTHING is
/// excluded and raw u = 0/4 = 0.0 (the other boundary). Both raw boundaries are hit, so this field
/// is SMOOTHING-DEPENDENT: primary smoothing (alpha=0.5) gives m=(2+0.5)/3=0.8333...,
/// u=(0+0.5)/5=0.1, agreement bits log2(0.8333/0.1)=3.05889..., disagreement bits
/// log2(0.1667/0.9)=-2.43296...; the secondary constant (alpha=1.0, classic Laplace) gives
/// m=(2+1)/4=0.75, u=(0+1)/6=0.16667, agreement bits log2(0.75/0.16667)=2.16993..., disagreement
/// bits log2(0.25/0.83333)=-1.73697... (all hand-computed, not asserted against the code's own
/// output).
/// </para>
/// <para>
/// Field "b" (exact): r5.b="P", r6.b="Q" (same-entity pair DISAGREES), r9.b=r10.b="P" (same-entity
/// pair agrees) — raw m = 1/2 = 0.5. The "block" origin's determination on "b": 2 of 4 different-
/// entity observations agree (rate 0.5) — below threshold, nothing excluded, raw u = 2/4 = 0.5.
/// m equals u exactly, even after smoothing ((1+0.5)/3 = (2+0.5)/5 = 0.5): this is the UNUSABLE
/// case (evidence would decrease as similarity increases), so no AgreementBits/DisagreementBits
/// are emitted for it at all.
/// </para>
/// <para>
/// Field "c" (exact): populated only on r5 and r10 ("V1" both) — a DIFFERENT-entity pair, and the
/// ONLY different-entity pair with "c" populated on both sides (r6 and r9 never carry "c"), so the
/// "block" origin has exactly ONE observation for field "c", and it happens to agree (rate =
/// 1/1 = 1.0). That single-observation rate is >= the determination threshold, so it IS excluded —
/// precisely the small-N instability the task calls out ("an origin with very few non-matched
/// candidates gives an unstable determination rate"): one coincidental agreement is enough to
/// exclude a field down to zero remaining observations. This is reported, not silently prevented.
/// Same-entity observations are also zero (neither r5-r6 nor r9-r10 has "c" on both sides), so "c"
/// ends up with NO ESTIMATE for both m and u.
/// </para>
/// </summary>
public class FieldEvidenceCalibrationServiceTests
{
    private static MatchingProfile Profile() => new()
    {
        ContentType = "person",
        Fields =
        [
            new ProfileField { Name = "block", SemanticType = SemanticFieldType.LastName,
                Roles = FieldRole.Matchable | FieldRole.Blocking, SimilarityEvaluator = "exact", Weight = 1.0 },
            new ProfileField { Name = "a", SemanticType = SemanticFieldType.FirstName,
                Roles = FieldRole.Matchable, SimilarityEvaluator = "exact", Weight = 1.0 },
            new ProfileField { Name = "b", SemanticType = SemanticFieldType.FirstName,
                Roles = FieldRole.Matchable, SimilarityEvaluator = "exact", Weight = 1.0 },
            new ProfileField { Name = "c", SemanticType = SemanticFieldType.FirstName,
                Roles = FieldRole.Matchable, SimilarityEvaluator = "exact", Weight = 1.0 }
        ],
        NormalizationStrategy = "identity",
        BlockingStrategies = ["token-name"],
        CandidateRetrievalStrategy = "linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = "weighted",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 0.5,
        ReviewThreshold = 0.3
    };

    private static EntityRecord Rec(string id, string block, string? a = null, string? b = null, string? c = null)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["block"] = block };
        if (a is not null) fields["a"] = a;
        if (b is not null) fields["b"] = b;
        if (c is not null) fields["c"] = c;
        return new EntityRecord
        {
            Id = Guid.NewGuid(), ProjectId = Guid.Empty, SourceId = Guid.Empty, IngestBatchId = Guid.Empty,
            SourceRecordId = id, Fields = fields, CreatedAt = DateTimeOffset.UnixEpoch
        };
    }

    // Fit half at fitFraction 0.5 (hand-verified against SHA-256("r5") etc. — see IsFitHalf test).
    private static readonly IReadOnlyList<EntityRecord> Records =
    [
        Rec("r5", "Smith", a: "X", b: "P", c: "V1"),   // fit, group G1
        Rec("r6", "Smith", a: "X", b: "Q"),             // fit, group G1
        Rec("r9", "Smith", a: "Y", b: "P"),             // fit, group G2
        Rec("r10", "Smith", a: "Y", b: "P", c: "V1"),   // fit, group G2
        Rec("e1", "Smith", a: "Z1"),                    // EVAL half, group G3 — must never be used
        Rec("e2", "Smith", a: "Z2")                     // EVAL half, group G3 — must never be used
    ];

    private static readonly Dictionary<string, string> Truth = new()
    {
        ["r5"] = "G1", ["r6"] = "G1", ["r9"] = "G2", ["r10"] = "G2", ["e1"] = "G3", ["e2"] = "G3"
    };

    private static FieldEvidenceCalibrationService NewService() => new(MatchingDefaults.CreateRegistry());

    private static FieldEvidenceCalibrationResult Run() =>
        NewService().Calibrate(Records, Profile(), Truth);

    // ---- IsFitHalf: determinism and the exact split this fixture depends on ----

    [Theory]
    [InlineData("r5", true)]
    [InlineData("r6", true)]
    [InlineData("r9", true)]
    [InlineData("r10", true)]
    [InlineData("e1", false)]
    [InlineData("e2", false)]
    public void IsFitHalf_MatchesHandComputedSha256Split(string id, bool expectedFit)
        => Assert.Equal(expectedFit, FieldEvidenceCalibrationService.IsFitHalf(id, 0.5));

    [Fact]
    public void IsFitHalf_IsDeterministic_AcrossRepeatedCalls()
    {
        var first = FieldEvidenceCalibrationService.IsFitHalf("some-record-id", 0.5);
        for (var i = 0; i < 5; i++)
            Assert.Equal(first, FieldEvidenceCalibrationService.IsFitHalf("some-record-id", 0.5));
    }

    // ---- Split bookkeeping ----

    [Fact]
    public void Calibrate_ReportsSplitSizes_AndCandidateCounts()
    {
        var result = Run();

        Assert.Equal(6, result.TotalRecords);
        Assert.Equal(4, result.FitRecords);
        Assert.Equal(2, result.EvalRecords);

        // Single blocking key ("name:smith") over the 4 fit-half records: C(4,2) = 6 pairs,
        // each owned by that one key, so occurrences and emitted pairs coincide.
        Assert.Equal(6, result.CandidateOccurrences);
        Assert.Equal(6, result.CandidatePairsEmitted);

        // r5-r6 and r9-r10 are same-entity; the other four fit-half pairs are different-entity.
        Assert.Equal(2, result.LabeledSameEntityPairs);
        Assert.Equal(4, result.LabeledDifferentEntityPairs);
        Assert.Equal(0, result.UnlabeledCandidatePairs);
    }

    // ---- Field "block": its own origin agrees on ITSELF 100% of the time (all 4 different-
    // entity candidates share "block"="Smith" by construction) -> ABOVE threshold -> EXCLUDED,
    // leaving nothing to estimate u from at all ----

    [Fact]
    public void Calibrate_FieldWhoseOwnOriginDeterminesIt_ExcludesAllItsDifferentEntityPairs()
    {
        var block = Run().Fields.Single(f => f.FieldName == "block");

        // Same-entity pairs are unaffected by exclusion (m is never filtered): both r5-r6 and
        // r9-r10 carry "block"="Smith" on both sides, so both agree.
        Assert.Equal(2, block.SameEntityComparisons);
        Assert.Equal(2, block.SameEntityAgreements);
        Assert.Equal(1.0, block.RawM);

        // All 4 different-entity candidates share "name:smith" — the only blocking key in this
        // profile — and ALL agree on "block" (it's the value that put them in the same block in
        // the first place). Measured determination rate for origin "block" on field "block" is
        // therefore 4/4 = 1.0, at/above the 0.95 threshold, so this origin's pairs are excluded,
        // leaving nothing to estimate u from. Distinct from SMOOTHING-DEPENDENT: there is no u to
        // depend on smoothing at all.
        Assert.Equal(0, block.DifferentEntityComparisons);
        Assert.Equal(0, block.DifferentEntityAgreements);
        Assert.Equal(4, block.DifferentEntityExcludedByDetermination);
        Assert.Null(block.RawU);
        Assert.Null(block.SmoothedU);

        var origin = Assert.Single(block.OriginDeterminations);
        Assert.Equal("block", origin.OriginLabel);
        Assert.Equal(4, origin.Observations);
        Assert.Equal(4, origin.Agreements);
        Assert.Equal(1.0, origin.DeterminationRate, 9);
        Assert.True(origin.Excluded);

        Assert.Null(block.AgreementBits);
        Assert.Null(block.DisagreementBits);
        Assert.True(block.Usable);          // missing data, not "evidence runs backwards"
        Assert.Null(block.UnusableReason);
        Assert.False(block.SmoothingDependent);
        Assert.Empty(block.SmoothingSensitivity);
    }

    // ---- Field "a": the "block" origin's determination on "a" is 0.0 (far below threshold) ----

    [Fact]
    public void Calibrate_OriginBelowThreshold_ForACrossField_DoesNotExclude()
    {
        var field = Run().Fields.Single(f => f.FieldName == "a");

        Assert.Equal(2, field.SameEntityComparisons);
        Assert.Equal(2, field.SameEntityAgreements);
        Assert.Equal(1.0, field.RawM);
        Assert.Equal(4, field.DifferentEntityComparisons);
        Assert.Equal(0, field.DifferentEntityAgreements);
        Assert.Equal(0, field.DifferentEntityExcludedByDetermination);
        Assert.Equal(0.0, field.RawU);

        var origin = Assert.Single(field.OriginDeterminations);
        Assert.Equal("block", origin.OriginLabel);
        Assert.Equal(4, origin.Observations);
        Assert.Equal(0, origin.Agreements);
        Assert.Equal(0.0, origin.DeterminationRate, 9);
        Assert.False(origin.Excluded);   // 0.0 << 0.95

        // Eval-half records e1/e2 disagree on "a"; if they leaked into this walk raw m would be
        // 2/3, not 1.0. This is the isolation guarantee, pinned as a number.
        Assert.NotNull(field.SmoothedM);
        Assert.Equal(2.5 / 3.0, field.SmoothedM!.Value, 9);
        Assert.NotNull(field.SmoothedU);
        Assert.Equal(0.5 / 5.0, field.SmoothedU!.Value, 9);

        Assert.True(field.Usable);
        Assert.Null(field.UnusableReason);
        Assert.NotNull(field.AgreementBits);
        Assert.Equal(3.05889368905357, field.AgreementBits!.Value, 9);
        Assert.NotNull(field.DisagreementBits);
        Assert.Equal(-2.43295940727611, field.DisagreementBits!.Value, 9);

        // raw u == 0 (and raw m == 1): without the continuity correction, agreement bits would be
        // log2(m/0) = +infinity. Flagged, and shown under >= 2 smoothing constants.
        Assert.True(field.SmoothingDependent);
        Assert.True(field.SmoothingSensitivity.Count >= 2);

        var primary = field.SmoothingSensitivity.Single(v => v.Alpha == 0.5);
        Assert.Equal(2.5 / 3.0, primary.SmoothedM, 9);
        Assert.Equal(0.5 / 5.0, primary.SmoothedU, 9);
        Assert.Equal(3.05889368905357, primary.AgreementBits!.Value, 9);
        Assert.Equal(-2.43295940727611, primary.DisagreementBits!.Value, 9);

        // Secondary constant (classic Laplace add-one): m=(2+1)/4=0.75, u=(0+1)/6=0.16667 — a
        // visibly different number from the primary, proving the sensitivity is real, not a
        // relabeled copy of the same figure.
        var secondary = field.SmoothingSensitivity.Single(v => v.Alpha == 1.0);
        Assert.Equal(0.75, secondary.SmoothedM, 9);
        Assert.Equal(1.0 / 6.0, secondary.SmoothedU, 9);
        Assert.Equal(2.16992500144231, secondary.AgreementBits!.Value, 9);
        Assert.Equal(-1.73696559416621, secondary.DisagreementBits!.Value, 9);
    }

    // ---- Field "b": m <= u, even after smoothing -> UNUSABLE, no parameters emitted ----

    [Fact]
    public void Calibrate_FieldWithMLessThanOrEqualU_IsUnusable_WithNoParametersEmitted()
    {
        var field = Run().Fields.Single(f => f.FieldName == "b");

        Assert.Equal(2, field.SameEntityComparisons);
        Assert.Equal(1, field.SameEntityAgreements);
        Assert.Equal(0.5, field.RawM);
        Assert.Equal(4, field.DifferentEntityComparisons);
        Assert.Equal(2, field.DifferentEntityAgreements);
        Assert.Equal(0.5, field.RawU);

        var origin = Assert.Single(field.OriginDeterminations);
        Assert.Equal("block", origin.OriginLabel);
        Assert.Equal(0.5, origin.DeterminationRate, 9);
        Assert.False(origin.Excluded);   // 0.5 << 0.95

        Assert.Equal(0.5, field.SmoothedM);
        Assert.Equal(0.5, field.SmoothedU);

        // Agreeing on "b" is worthless (indeed backwards) evidence here (m == u): the field must
        // be refused outright, not have bits computed and merely flagged.
        Assert.False(field.Usable);
        Assert.Null(field.AgreementBits);
        Assert.Null(field.DisagreementBits);
        Assert.NotNull(field.UnusableReason);
        Assert.Contains("DECREASES", field.UnusableReason, StringComparison.Ordinal);

        // Neither raw m nor raw u for "b" hits the 1/0 boundary, so this field is not also flagged
        // smoothing-dependent — the two guard rails are independent.
        Assert.False(field.SmoothingDependent);
        Assert.Empty(field.SmoothingSensitivity);
    }

    // ---- Field "c": zero same-entity observations, AND a small-N (n=1) origin whose rate
    // happens to hit 1.0 -> excluded, illustrating the instability the task warns about ----

    [Fact]
    public void Calibrate_SmallNOrigin_CanExcludeAllData_ButIsReportedRatherThanHidden()
    {
        var field = Run().Fields.Single(f => f.FieldName == "c");

        Assert.Equal(0, field.SameEntityComparisons);
        Assert.Equal(0, field.SameEntityAgreements);
        Assert.Null(field.RawM);
        Assert.Null(field.SmoothedM);

        // Exactly one different-entity pair (r5,r10) has "c" populated on both sides, and it
        // agrees — a determination rate of 1.0 computed from a SINGLE observation. Excluded
        // anyway, per the 0.95 rule as specified; the observation count is what a reader needs to
        // judge whether that exclusion should be trusted, and it is right here, not hidden.
        var origin = Assert.Single(field.OriginDeterminations);
        Assert.Equal("block", origin.OriginLabel);
        Assert.Equal(1, origin.Observations);
        Assert.Equal(1, origin.Agreements);
        Assert.Equal(1.0, origin.DeterminationRate, 9);
        Assert.True(origin.Excluded);

        Assert.Equal(0, field.DifferentEntityComparisons);
        Assert.Equal(0, field.DifferentEntityAgreements);
        Assert.Equal(1, field.DifferentEntityExcludedByDetermination);
        Assert.Null(field.RawU);
        Assert.Null(field.SmoothedU);

        // Bits need BOTH m and u; with both undefined, both must be null, not computed against a
        // fabricated stand-in.
        Assert.Null(field.AgreementBits);
        Assert.Null(field.DisagreementBits);

        // Missing data is not the same guard rail as "evidence runs backwards": with no
        // same-entity observations there is nothing to compare m and u against, so this field is
        // still nominally Usable (just unestimated) rather than refused.
        Assert.True(field.Usable);
        Assert.Null(field.UnusableReason);
        Assert.False(field.SmoothingDependent);
    }

    // ---- Similarity distribution ----

    [Fact]
    public void Calibrate_HistogramBuckets_SumToTheReportedComparisonCounts()
    {
        var result = Run();
        foreach (var field in result.Fields)
        {
            Assert.Equal(field.SameEntityComparisons, field.SameEntitySimilarityHistogram.Sum());
            Assert.Equal(field.DifferentEntityComparisons, field.DifferentEntitySimilarityHistogram.Sum());
        }

        // Field "a": all 2 same-entity observations are exact agreement (1.0) -> last bucket;
        // all 4 different-entity observations are exact disagreement (0.0) -> first bucket.
        var a = result.Fields.Single(f => f.FieldName == "a");
        Assert.Equal(2, a.SameEntitySimilarityHistogram[^1]);
        Assert.Equal(4, a.DifferentEntitySimilarityHistogram[0]);
    }

    // ---- Validation ----

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void Calibrate_InvalidFitFraction_Throws(double fraction)
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            NewService().Calibrate(Records, Profile(), Truth, fitFraction: fraction));

    [Fact]
    public void Calibrate_NonIdentityNormalization_Throws()
    {
        var profile = Profile() with { NormalizationStrategy = "not-identity" };
        var ex = Assert.Throws<ArgumentException>(() => NewService().Calibrate(Records, profile, Truth));
        Assert.Contains("identity", ex.Message);
    }

    [Fact]
    public void Calibrate_NonFieldWeightedSimilarity_Throws()
    {
        var profile = Profile() with { SimilarityStrategy = "not-field-weighted" };
        var ex = Assert.Throws<ArgumentException>(() => NewService().Calibrate(Records, profile, Truth));
        Assert.Contains("field-weighted", ex.Message);
    }

    [Fact]
    public void Calibrate_DuplicateSourceRecordId_Throws()
    {
        var duped = new List<EntityRecord>(Records) { Rec("r5", "Smith", a: "X") };
        var ex = Assert.Throws<ArgumentException>(() => NewService().Calibrate(duped, Profile(), Truth));
        Assert.Contains("r5", ex.Message);
    }

    // =====================================================================================
    // Determination-based exclusion, above threshold: two DIFFERENT blocking fields, each
    // excluding only ITS OWN self-determining pairs, never the other's.
    //
    // Fixture: TWO blocking fields ("dob", exact-value via DateOfBirth semantic type; "ident",
    // exact-value via the Identifier role) plus one Matchable-only field ("other", never a
    // blocking key) that must be completely unaffected. exact-value keys are "{field}:{value}",
    // so ownership never has to be inferred — each field's key vocabulary is disjoint by
    // construction here, which is what makes the six candidate pairs below unambiguous:
    //
    //   r5/r6/r11 share "dob:19800101"   -> pairs (r5,r6) (r5,r11) (r6,r11), owned by "dob".
    //   r9/r10/r12 share "ident:z"        -> pairs (r9,r10) (r9,r12) (r10,r12), owned by "ident".
    //
    // Truth: r5/r6 = group GA (a true pair), r9/r10 = group GB (a true pair), r11 = group GC,
    // r12 = group GD (GC and GD each have no partner in this fixture, so they contribute no
    // same-entity pairs of their own — only cross pairs against GA/GB).
    //
    // Field "dob": same-entity pairs are (r5,r6) [dob agrees, both 19800101] and (r9,r10) [dob
    // disagrees, 19900505 vs 19910606] -> SameCount=2, SameAgree=1 (m=0.5 after smoothing).
    // Different-entity pairs, by ORIGIN: origin "dob" owns (r5,r11) and (r6,r11), BOTH of which
    // agree on dob (all three share 19800101) -> determination 2/2=1.0 -> EXCLUDED. Origin "ident"
    // owns (r9,r12) and (r10,r12), BOTH of which disagree on dob -> determination 0/2=0.0 ->
    // KEPT. So post-exclusion DiffCount=2, DiffAgree=0 (raw u=0.0); the excluded origin's pairs
    // were exactly the two that agreed, so WITHOUT this exclusion raw u would have been
    // 2/4=0.5 -- equal to m, i.e. "dob" would have looked UNUSABLE purely from measuring the
    // field against pairs its own key selected (the shape of the real last_name finding).
    //
    // Field "ident" is the mirror image: same-entity (r5,r6) disagree (A vs B), (r9,r10) agree
    // (Z vs Z) -> SameCount=2, SameAgree=1. Origin "ident" owns (r9,r12)/(r10,r12), BOTH agree on
    // ident (all three share "Z") -> determination 2/2=1.0 -> EXCLUDED. Origin "dob" owns
    // (r5,r11)/(r6,r11), BOTH disagree on ident -> determination 0/2=0.0 -> KEPT. Same numbers as
    // "dob" by the fixture's deliberate symmetry: post-exclusion DiffCount=2, DiffAgree=0.
    //
    // Field "other" (NOT a blocking key, so never itself an origin): origin "dob" owns
    // (r5,r11)/(r6,r11) — determination on "other": (r5,r11) P/Q disagree, (r6,r11) Q/Q agree ->
    // 1/2=0.5 -> KEPT. Origin "ident" owns (r9,r12)/(r10,r12) — determination on "other": both
    // disagree (P/X, P/X) -> 0/2=0.0 -> KEPT. Neither origin crosses 0.95 for "other", so ALL FOUR
    // different-entity pairs count: DiffCount=4, DiffAgree=1 (raw u=0.25) -- unaffected by
    // determination-based exclusion regardless of which field owns which pair.
    // =====================================================================================

    private static MatchingProfile ExclusionProfile() => new()
    {
        ContentType = "person",
        Fields =
        [
            new ProfileField { Name = "dob", SemanticType = SemanticFieldType.DateOfBirth,
                Roles = FieldRole.Matchable | FieldRole.Blocking, SimilarityEvaluator = "exact", Weight = 1.0 },
            new ProfileField { Name = "ident", SemanticType = SemanticFieldType.SourceIdentifier,
                Roles = FieldRole.Matchable | FieldRole.Blocking | FieldRole.Identifier,
                SimilarityEvaluator = "exact", Weight = 1.0 },
            new ProfileField { Name = "other", SemanticType = SemanticFieldType.FirstName,
                Roles = FieldRole.Matchable, SimilarityEvaluator = "exact", Weight = 1.0 }
        ],
        NormalizationStrategy = "identity",
        BlockingStrategies = ["exact-value"],
        CandidateRetrievalStrategy = "linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = "weighted",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 0.5,
        ReviewThreshold = 0.3
    };

    private static EntityRecord ExclusionRec(string id, string dob, string ident, string other)
        => new()
        {
            Id = Guid.NewGuid(), ProjectId = Guid.Empty, SourceId = Guid.Empty, IngestBatchId = Guid.Empty,
            SourceRecordId = id,
            Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                { ["dob"] = dob, ["ident"] = ident, ["other"] = other },
            CreatedAt = DateTimeOffset.UnixEpoch
        };

    // All six ids are fit-half at fitFraction 0.5 (hand-verified, same as the fixture above).
    private static readonly IReadOnlyList<EntityRecord> ExclusionRecords =
    [
        ExclusionRec("r5", "19800101", "A", "P"),   // group GA
        ExclusionRec("r6", "19800101", "B", "Q"),   // group GA (true pair with r5)
        ExclusionRec("r9", "19900505", "Z", "P"),   // group GB
        ExclusionRec("r10", "19910606", "Z", "P"),  // group GB (true pair with r9)
        ExclusionRec("r11", "19800101", "C", "Q"),  // group GC
        ExclusionRec("r12", "19750303", "Z", "X")   // group GD
    ];

    private static readonly Dictionary<string, string> ExclusionTruth = new()
    {
        ["r5"] = "GA", ["r6"] = "GA", ["r9"] = "GB", ["r10"] = "GB", ["r11"] = "GC", ["r12"] = "GD"
    };

    private static FieldEvidenceCalibrationResult RunExclusion() =>
        NewService().Calibrate(ExclusionRecords, ExclusionProfile(), ExclusionTruth);

    [Fact]
    public void Calibrate_ExclusionFixture_SplitAndCandidateCounts()
    {
        var result = RunExclusion();

        Assert.Equal(6, result.TotalRecords);
        Assert.Equal(6, result.FitRecords);
        Assert.Equal(0, result.EvalRecords);
        Assert.Equal(6, result.CandidatePairsEmitted);
        Assert.Equal(2, result.LabeledSameEntityPairs);
        Assert.Equal(4, result.LabeledDifferentEntityPairs);

        // Every one of the 6 pairs is owned by exactly one field's key by construction (see the
        // fixture doc above) -- nothing here should register as ambiguous.
        Assert.Equal(0, result.UnattributableOwnerCandidatePairs);
    }

    [Fact]
    public void Calibrate_OriginAboveThreshold_Excludes_AndUDiffersFromUnexcluded()
    {
        var dob = RunExclusion().Fields.Single(f => f.FieldName == "dob");

        Assert.Equal(2, dob.SameEntityComparisons);
        Assert.Equal(1, dob.SameEntityAgreements);
        Assert.Equal(0.5, dob.RawM);

        // Two origins own dob's different-entity observations: "dob" itself (2 obs, both agree,
        // rate 1.0 -> excluded) and "ident" (2 obs, both disagree, rate 0.0 -> kept).
        Assert.Equal(2, dob.OriginDeterminations.Count);
        var dobOrigin = dob.OriginDeterminations.Single(o => o.OriginLabel == "dob");
        Assert.Equal(2, dobOrigin.Observations);
        Assert.Equal(2, dobOrigin.Agreements);
        Assert.Equal(1.0, dobOrigin.DeterminationRate, 9);
        Assert.True(dobOrigin.Excluded);
        var identOrigin = dob.OriginDeterminations.Single(o => o.OriginLabel == "ident");
        Assert.Equal(2, identOrigin.Observations);
        Assert.Equal(0, identOrigin.Agreements);
        Assert.Equal(0.0, identOrigin.DeterminationRate, 9);
        Assert.False(identOrigin.Excluded);

        // Post-exclusion: 2 different-entity comparisons remain (both disagree), 2 were excluded
        // (both of which agreed). Without the exclusion this would have been DiffCount=4,
        // DiffAgree=2, raw u=0.5 -- equal to m, i.e. UNUSABLE purely from measuring the field
        // against pairs its own key selected.
        Assert.Equal(2, dob.DifferentEntityComparisons);
        Assert.Equal(0, dob.DifferentEntityAgreements);
        Assert.Equal(2, dob.DifferentEntityExcludedByDetermination);
        Assert.Equal(0.0, dob.RawU);

        Assert.NotNull(dob.SmoothedM);
        Assert.Equal(0.5, dob.SmoothedM!.Value, 9);
        Assert.NotNull(dob.SmoothedU);
        Assert.Equal(1.0 / 6.0, dob.SmoothedU!.Value, 9);

        Assert.True(dob.Usable);
        Assert.NotNull(dob.AgreementBits);
        Assert.Equal(1.58496250072116, dob.AgreementBits!.Value, 9);
        Assert.NotNull(dob.DisagreementBits);
        Assert.Equal(-0.736965594166206, dob.DisagreementBits!.Value, 9);

        // raw u == 0 post-exclusion: also correctly flagged smoothing-dependent.
        Assert.True(dob.SmoothingDependent);
        Assert.NotEmpty(dob.SmoothingSensitivity);
    }

    [Fact]
    public void Calibrate_SecondFieldOwningADifferentBlock_AlsoExcludesOnlyItsOwnPairs()
    {
        // Mirror of the "dob" case: "ident" owns the OTHER block (r9/r10/r12 sharing "ident:z"),
        // so its own excluded pairs are (r9,r12) and (r10,r12), not (r5,r11)/(r6,r11) -- those
        // are excluded from "dob"'s u, not "ident"'s. Same numbers by the fixture's symmetry, but
        // computed from a disjoint set of excluded pairs, which is the actual thing under test:
        // origin attribution must not confuse the two blocking fields with each other.
        var ident = RunExclusion().Fields.Single(f => f.FieldName == "ident");

        Assert.Equal(2, ident.SameEntityComparisons);
        Assert.Equal(1, ident.SameEntityAgreements);
        Assert.Equal(0.5, ident.RawM);

        var identOrigin = ident.OriginDeterminations.Single(o => o.OriginLabel == "ident");
        Assert.Equal(1.0, identOrigin.DeterminationRate, 9);
        Assert.True(identOrigin.Excluded);
        var dobOrigin = ident.OriginDeterminations.Single(o => o.OriginLabel == "dob");
        Assert.Equal(0.0, dobOrigin.DeterminationRate, 9);
        Assert.False(dobOrigin.Excluded);

        Assert.Equal(2, ident.DifferentEntityComparisons);
        Assert.Equal(0, ident.DifferentEntityAgreements);
        Assert.Equal(2, ident.DifferentEntityExcludedByDetermination);
        Assert.Equal(0.0, ident.RawU);

        Assert.True(ident.Usable);
        Assert.NotNull(ident.AgreementBits);
        Assert.Equal(1.58496250072116, ident.AgreementBits!.Value, 9);
        Assert.NotNull(ident.DisagreementBits);
        Assert.Equal(-0.736965594166206, ident.DisagreementBits!.Value, 9);
        Assert.True(ident.SmoothingDependent);
    }

    [Fact]
    public void Calibrate_NonBlockingField_IsUnaffectedByExclusion()
    {
        var other = RunExclusion().Fields.Single(f => f.FieldName == "other");

        Assert.Equal(2, other.SameEntityComparisons);
        Assert.Equal(1, other.SameEntityAgreements);
        Assert.Equal(0.5, other.RawM);

        // Neither origin ("dob" at rate 0.5, "ident" at rate 0.0) crosses the determination
        // threshold for "other", so ALL FOUR different-entity candidate pairs count, none
        // excluded. This is the number this instrument reported before determination-based
        // exclusion existed, unchanged — "other" is never itself an origin.
        Assert.Equal(2, other.OriginDeterminations.Count);
        Assert.All(other.OriginDeterminations, o => Assert.False(o.Excluded));
        var dobOrigin = other.OriginDeterminations.Single(o => o.OriginLabel == "dob");
        Assert.Equal(0.5, dobOrigin.DeterminationRate, 9);
        var identOrigin = other.OriginDeterminations.Single(o => o.OriginLabel == "ident");
        Assert.Equal(0.0, identOrigin.DeterminationRate, 9);

        Assert.Equal(4, other.DifferentEntityComparisons);
        Assert.Equal(1, other.DifferentEntityAgreements);
        Assert.Equal(0, other.DifferentEntityExcludedByDetermination);
        Assert.Equal(0.25, other.RawU);

        Assert.NotNull(other.SmoothedM);
        Assert.Equal(0.5, other.SmoothedM!.Value, 9);
        Assert.NotNull(other.SmoothedU);
        Assert.Equal(0.3, other.SmoothedU!.Value, 9);

        Assert.True(other.Usable);
        Assert.NotNull(other.AgreementBits);
        Assert.Equal(0.736965594166206, other.AgreementBits!.Value, 9);
        Assert.NotNull(other.DisagreementBits);
        Assert.Equal(-0.485426827170242, other.DisagreementBits!.Value, 9);

        // Neither raw m (0.5) nor raw u (0.25) hits a 0/1 boundary.
        Assert.False(other.SmoothingDependent);
        Assert.Empty(other.SmoothingSensitivity);
    }

    // =====================================================================================
    // Determination-based exclusion, BELOW threshold on a field's OWN origin: the SEC
    // organization_name case in miniature. A field can be the sole source of its own blocking
    // key and STILL have that origin measure well below the threshold, when the key captures
    // only PARTIAL signal (here: last name-blocking's LAST TOKEN of a multi-token value) rather
    // than the full value. Provenance says the key "derives from" company; the empirical rate
    // says it does not "determine" company — and the rate wins.
    //
    // Fixture: one blocking field, "company" (OrganizationName semantic type, so "token-name"
    // blocking keys off its LAST TOKEN), six records that all end in " Corp" and therefore all
    // share the single key "name:corp" -> one block of size 6 -> C(6,2) = 15 candidate pairs, ALL
    // owned by "company" (the sole blocking field, mirroring SEC's profile shape).
    //
    // Truth: r5/r6 = group GA (true pair, both "Acme Corp"), r9/r10 = group GB (true pair, both
    // "Beta Corp"), r11 = group GC ("Gamma Corp", singleton), r12 = group GD ("Delta Corp",
    // singleton). Same-entity pairs: (r5,r6) and (r9,r10), both with IDENTICAL company strings ->
    // SameCount=2, SameAgree=2 (raw m=1.0). The other 13 pairs are different-entity, and every
    // one of the six records has a DISTINCT full company string except within its own group — so
    // NONE of the 13 different-entity pairs share an identical exact value: determination on
    // "company" for the (sole) "company" origin = 0/13 = 0.0, far below threshold -> KEPT, in
    // full: DiffCount=13, DiffAgree=0 (raw u=0.0). This is the falsifiable prediction the fix is
    // for: a field can own 100% of its candidates and still have its full un-excluded population
    // survive, when sharing its key does not actually force the field to agree.
    // =====================================================================================

    private static MatchingProfile PartialSignalProfile() => new()
    {
        ContentType = "organization",
        Fields =
        [
            new ProfileField { Name = "company", SemanticType = SemanticFieldType.OrganizationName,
                Roles = FieldRole.Matchable | FieldRole.Blocking, SimilarityEvaluator = "exact", Weight = 1.0 }
        ],
        NormalizationStrategy = "identity",
        BlockingStrategies = ["token-name"],
        CandidateRetrievalStrategy = "linear",
        SimilarityStrategy = "field-weighted",
        ScoringStrategy = "weighted",
        DecisionStrategy = "threshold",
        ClusteringStrategy = "union-find",
        AutoMatchThreshold = 0.5,
        ReviewThreshold = 0.3
    };

    private static EntityRecord PartialSignalRec(string id, string company)
        => new()
        {
            Id = Guid.NewGuid(), ProjectId = Guid.Empty, SourceId = Guid.Empty, IngestBatchId = Guid.Empty,
            SourceRecordId = id,
            Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["company"] = company },
            CreatedAt = DateTimeOffset.UnixEpoch
        };

    // All six ids are fit-half at fitFraction 0.5 (same ids as the exclusion fixture above).
    private static readonly IReadOnlyList<EntityRecord> PartialSignalRecords =
    [
        PartialSignalRec("r5", "Acme Corp"),    // group GA
        PartialSignalRec("r6", "Acme Corp"),    // group GA (true pair with r5)
        PartialSignalRec("r9", "Beta Corp"),    // group GB
        PartialSignalRec("r10", "Beta Corp"),   // group GB (true pair with r9)
        PartialSignalRec("r11", "Gamma Corp"),  // group GC
        PartialSignalRec("r12", "Delta Corp")   // group GD
    ];

    private static readonly Dictionary<string, string> PartialSignalTruth = new()
    {
        ["r5"] = "GA", ["r6"] = "GA", ["r9"] = "GB", ["r10"] = "GB", ["r11"] = "GC", ["r12"] = "GD"
    };

    [Fact]
    public void Calibrate_OriginBelowThreshold_OnItsOwnField_DoesNotExclude()
    {
        var result = NewService().Calibrate(PartialSignalRecords, PartialSignalProfile(), PartialSignalTruth);

        // One key ("name:corp") over all 6 records: C(6,2) = 15 candidate pairs.
        Assert.Equal(15, result.CandidatePairsEmitted);
        Assert.Equal(2, result.LabeledSameEntityPairs);
        Assert.Equal(13, result.LabeledDifferentEntityPairs);
        Assert.Equal(0, result.UnattributableOwnerCandidatePairs);

        var company = Assert.Single(result.Fields);
        Assert.Equal(2, company.SameEntityComparisons);
        Assert.Equal(2, company.SameEntityAgreements);
        Assert.Equal(1.0, company.RawM);

        // "company" is the sole blocking field, so ALL 13 different-entity pairs are owned by its
        // own origin -- but not one of them shares an identical full company string (every group
        // has a distinct name), so the MEASURED determination rate is 0/13 = 0.0. Nowhere near
        // the 0.95 threshold: nothing is excluded, unlike a provenance-only rule that would have
        // excluded all 13 purely because "company" derived its own blocking key.
        var origin = Assert.Single(company.OriginDeterminations);
        Assert.Equal("company", origin.OriginLabel);
        Assert.Equal(13, origin.Observations);
        Assert.Equal(0, origin.Agreements);
        Assert.Equal(0.0, origin.DeterminationRate, 9);
        Assert.False(origin.Excluded);

        Assert.Equal(13, company.DifferentEntityComparisons);
        Assert.Equal(0, company.DifferentEntityAgreements);
        Assert.Equal(0, company.DifferentEntityExcludedByDetermination);
        Assert.Equal(0.0, company.RawU);

        // Both raw m == 1 and raw u == 0 here too (a coincidence of this small fixture, not a
        // requirement of the below-threshold behavior under test) -- still usable, still reports
        // smoothing sensitivity, exactly like field "a" above.
        Assert.True(company.Usable);
        Assert.True(company.SmoothingDependent);
        Assert.NotEmpty(company.SmoothingSensitivity);
    }
}
