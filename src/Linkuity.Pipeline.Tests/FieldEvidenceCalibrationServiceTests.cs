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
/// same-entity (2 total); the other four (r5-r9, r5-r10, r6-r9, r6-r10) are different-entity.
/// </para>
/// <para>
/// Field "a" (exact): r5.a=r6.a="X", r9.a=r10.a="Y" — every same-entity pair agrees (raw m = 2/2
/// = 1.0, the boundary FieldEvidence refuses), every different-entity pair disagrees (raw u =
/// 0/4 = 0.0, the other boundary). Both raw boundaries are hit, so this field is SMOOTHING-
/// DEPENDENT: primary smoothing (alpha=0.5) gives m=(2+0.5)/3=0.8333..., u=(0+0.5)/5=0.1, agreement
/// bits log2(0.8333/0.1)=3.05889..., disagreement bits log2(0.1667/0.9)=-2.43296...; the secondary
/// constant (alpha=1.0, classic Laplace) gives m=(2+1)/4=0.75, u=(0+1)/6=0.16667, agreement bits
/// log2(0.75/0.16667)=2.16993..., disagreement bits log2(0.25/0.83333)=-1.73697... (all
/// hand-computed, not asserted against the code's own output).
/// </para>
/// <para>
/// Field "b" (exact): r5.b="P", r6.b="Q" (same-entity pair DISAGREES), r9.b=r10.b="P" (same-entity
/// pair agrees) — raw m = 1/2 = 0.5. Cross pairs: r5-r9 and r5-r10 agree (both "P"), r6-r9 and
/// r6-r10 disagree ("Q" vs "P") — raw u = 2/4 = 0.5. m equals u exactly, even after smoothing
/// ((1+0.5)/3 = (2+0.5)/5 = 0.5): this is the UNUSABLE case (evidence would decrease as similarity
/// increases), so no AgreementBits/DisagreementBits are emitted for it at all.
/// </para>
/// <para>
/// Field "c" (exact): populated only on r5 and r10 ("V1" both), which is a DIFFERENT-entity pair
/// — neither same-entity pair (r5-r6, r9-r10) has both sides populated, so field "c" has zero
/// same-entity observations. Its raw/smoothed m must be null (not zero, not fabricated), and its
/// bits must be null since one side of the ratio is undefined.
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

    // ---- Field "a": raw u == 0 (and raw m == 1) -> SMOOTHING-DEPENDENT, multiple constants shown ----

    [Fact]
    public void Calibrate_FieldWithRawUZero_IsSmoothingDependent_WithMultipleConstantsShown()
    {
        var field = Run().Fields.Single(f => f.FieldName == "a");

        Assert.Equal(2, field.SameEntityComparisons);
        Assert.Equal(2, field.SameEntityAgreements);
        Assert.Equal(1.0, field.RawM);
        Assert.Equal(4, field.DifferentEntityComparisons);
        Assert.Equal(0, field.DifferentEntityAgreements);
        Assert.Equal(0.0, field.RawU);

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

    // ---- Field "c": zero same-entity observations ----

    [Fact]
    public void Calibrate_FieldWithNoSameEntityObservations_ReportsNullRatherThanFabricated()
    {
        var field = Run().Fields.Single(f => f.FieldName == "c");

        Assert.Equal(0, field.SameEntityComparisons);
        Assert.Equal(0, field.SameEntityAgreements);
        Assert.Null(field.RawM);
        Assert.Null(field.SmoothedM);

        Assert.Equal(1, field.DifferentEntityComparisons);
        Assert.Equal(1, field.DifferentEntityAgreements);
        Assert.Equal(1.0, field.RawU);
        Assert.Equal(0.75, field.SmoothedU); // (1+0.5)/(1+1)

        // Bits need BOTH m and u; with m undefined, both must be null, not computed against a
        // fabricated stand-in for the missing side.
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
}
