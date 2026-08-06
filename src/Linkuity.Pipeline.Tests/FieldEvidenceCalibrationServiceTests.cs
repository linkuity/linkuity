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
/// Field "block" is itself a blocking-role field, and its own origin agrees with itself on EVERY
/// candidate (same- and different-entity alike — it's the reason those pairs exist at all), so its
/// own-origin determination is 1.0: BOTH the different-entity pairs AND the matching same-entity
/// pairs are excluded from "block"'s conditioned m/u, leaving nothing at all (NO ESTIMATE).
/// </para>
/// <para>
/// Field "a" (exact, NOT itself a blocking field): r5.a=r6.a="X", r9.a=r10.a="Y" — every
/// same-entity pair agrees (raw m = 2/2 = 1.0, the boundary FieldEvidence refuses). "a" is never
/// its own origin (it has no blocking key at all), so NOTHING is ever excluded for it regardless
/// of how the "block" origin measures — conditioned and unconditioned are identical, by
/// construction. Raw u = 0/4 = 0.0 (the other boundary). Both raw boundaries are hit, so this
/// field is SMOOTHING-DEPENDENT: primary smoothing (alpha=0.5) gives m=(2+0.5)/3=0.8333...,
/// u=(0+0.5)/5=0.1, agreement bits log2(0.8333/0.1)=3.05889..., disagreement bits
/// log2(0.1667/0.9)=-2.43296...; the secondary constant (alpha=1.0, classic Laplace) gives
/// m=(2+1)/4=0.75, u=(0+1)/6=0.16667, agreement bits log2(0.75/0.16667)=2.16993..., disagreement
/// bits log2(0.25/0.83333)=-1.73697... (all hand-computed, not asserted against the code's own
/// output).
/// </para>
/// <para>
/// Field "b" (exact, not a blocking field): r5.b="P", r6.b="Q" (same-entity pair DISAGREES),
/// r9.b=r10.b="P" (same-entity pair agrees) — raw m = 1/2 = 0.5. Cross pairs: r5-r9 and r5-r10
/// agree (both "P"), r6-r9 and r6-r10 disagree ("Q" vs "P") — raw u = 2/4 = 0.5. m equals u
/// exactly, even after smoothing: this is the UNUSABLE case (evidence would decrease as
/// similarity increases), so no AgreementBits/DisagreementBits are emitted for it at all.
/// </para>
/// <para>
/// Field "c" (exact, not a blocking field): populated only on r5 and r10 ("V1" both), which is a
/// DIFFERENT-entity pair owned by "block" — and "block"'s determination on "c" from that single
/// observation is 1.0 (the only observation happens to agree). Under a rule that excludes ANY
/// origin crossing the threshold (the bug this round fixes), that single coincidence would wipe
/// out "c"'s entire u. Because exclusion is now restricted to a field's OWN origin, and "c" has
/// none, nothing is excluded here no matter how extreme "block"'s rate is: this is the small-N
/// cross-field trap the fix closes, kept as a regression test. Same-entity observations are also
/// zero (neither r5-r6 nor r9-r10 has "c" on both sides), so "c" ends up with NO ESTIMATE for m,
/// but a perfectly ordinary (unfiltered) u = 1/1.
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

    /// <summary>
    /// The guarantee every downstream "evaluated on held-out data" claim rests on: for ANY id and
    /// ANY valid fitFraction, an id lands on EXACTLY one side (fit XOR eval), and no id is ever
    /// dropped by the split — the fit half and the eval half partition the corpus. Proven over a
    /// few hundred synthetic ids rather than just the 6-record fixture above, so this is a
    /// property of the function itself, not an artifact of one hand-picked set. This is also what
    /// makes `AuditCliCommon.ApplyEvalOnlyFilter` (Linkuity.Cli) safe to build on top of
    /// <see cref="FieldEvidenceCalibrationService.IsFitHalf"/> as-is: the eval-only filter keeps
    /// exactly the ids this method says are NOT fit, and this test is the proof that "not fit" and
    /// "eval" are the same thing with nothing lost in between.
    /// </summary>
    [Theory]
    [InlineData(0.1)]
    [InlineData(0.5)]
    [InlineData(0.9)]
    public void IsFitHalf_FitAndEvalHalves_AreDisjoint_AndTheirUnionIsTheWholeCorpus(double fitFraction)
    {
        var ids = Enumerable.Range(0, 500).Select(i => $"record-{i:D4}").ToList();

        var fit = ids.Where(id => FieldEvidenceCalibrationService.IsFitHalf(id, fitFraction))
            .ToHashSet(StringComparer.Ordinal);
        var eval = ids.Where(id => !FieldEvidenceCalibrationService.IsFitHalf(id, fitFraction))
            .ToHashSet(StringComparer.Ordinal);

        // Disjoint: no id is on both sides.
        Assert.Empty(fit.Intersect(eval, StringComparer.Ordinal));
        // No id is dropped: every id in the corpus is on exactly one side, so the two counts sum
        // to the corpus size...
        Assert.Equal(ids.Count, fit.Count + eval.Count);
        // ...and the union recovers the whole corpus exactly, not merely the same cardinality.
        Assert.Equal(new HashSet<string>(ids, StringComparer.Ordinal), fit.Union(eval, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal));

        // Not a degenerate split that puts everything on one side (would trivially "pass" the
        // disjoint/union checks above without actually splitting anything).
        Assert.NotEmpty(fit);
        Assert.NotEmpty(eval);
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

    // ---- Field "block": its own origin agrees on ITSELF 100% of the time on BOTH same- and
    // different-entity pairs (all 6 candidates share "block"="Smith" by construction) -> its own
    // origin is excluded from BOTH m and u, leaving nothing at all ----

    [Fact]
    public void Calibrate_FieldWhoseOwnOriginDeterminesIt_ExcludesFromBothMAndU()
    {
        var block = Run().Fields.Single(f => f.FieldName == "block");

        // Unconditioned m (every same-entity observation, unfiltered) is still 1.0 — reported for
        // comparison, but it is NOT what would feed AgreementBits.
        Assert.Equal(2, block.UnconditionedSameEntityComparisons);
        Assert.Equal(2, block.UnconditionedSameEntityAgreements);
        Assert.Equal(1.0, block.UnconditionedRawM);

        // Conditioned m: both same-entity pairs (r5-r6, r9-r10) are owned by "block"'s own origin
        // (all six candidates share the single "name:smith" key), and that origin's determination
        // on "block" is 1.0 (>= threshold) — so they are excluded from CONDITIONED m too, exactly
        // as the matching different-entity pairs are excluded from u.
        Assert.Equal(0, block.SameEntityComparisons);
        Assert.Equal(0, block.SameEntityAgreements);
        Assert.Equal(2, block.SameEntityExcludedByDetermination);
        Assert.Null(block.RawM);
        Assert.Null(block.SmoothedM);

        // All 4 different-entity candidates are excluded the same way, leaving nothing for u.
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
        Assert.False(block.SmoothedOrderingDiffersFromRaw);
        Assert.Empty(block.SmoothingSensitivity);
    }

    // ---- Field "a": the "block" origin's determination on "a" is 0.0 (far below threshold), and
    // "a" is never itself an origin -> conditioned == unconditioned in every respect ----

    [Fact]
    public void Calibrate_OriginBelowThreshold_ForACrossField_DoesNotExclude()
    {
        var field = Run().Fields.Single(f => f.FieldName == "a");

        Assert.Equal(2, field.SameEntityComparisons);
        Assert.Equal(2, field.SameEntityAgreements);
        Assert.Equal(1.0, field.RawM);
        Assert.Equal(0, field.SameEntityExcludedByDetermination);
        // "a" never has its own origin, so nothing is ever excluded: conditioned == unconditioned.
        Assert.Equal(field.SameEntityComparisons, field.UnconditionedSameEntityComparisons);
        Assert.Equal(field.SameEntityAgreements, field.UnconditionedSameEntityAgreements);
        Assert.Equal(field.RawM, field.UnconditionedRawM);
        Assert.Equal(field.SmoothedM, field.UnconditionedSmoothedM);

        Assert.Equal(4, field.DifferentEntityComparisons);
        Assert.Equal(0, field.DifferentEntityAgreements);
        Assert.Equal(0, field.DifferentEntityExcludedByDetermination);
        Assert.Equal(0.0, field.RawU);

        var origin = Assert.Single(field.OriginDeterminations);
        Assert.Equal("block", origin.OriginLabel);
        Assert.Equal(4, origin.Observations);
        Assert.Equal(0, origin.Agreements);
        Assert.Equal(0.0, origin.DeterminationRate, 9);
        Assert.False(origin.Excluded);   // "block" != "a": can never exclude, regardless of rate

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
        // log2(m/0) = +infinity. Flagged, and shown under >= 2 smoothing constants. Raw and
        // smoothed orderings agree (m > u on both), so no ordering-flip flag.
        Assert.True(field.SmoothingDependent);
        Assert.False(field.SmoothedOrderingDiffersFromRaw);
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
        Assert.Equal(0, field.SameEntityExcludedByDetermination);
        Assert.Equal(4, field.DifferentEntityComparisons);
        Assert.Equal(2, field.DifferentEntityAgreements);
        Assert.Equal(0.5, field.RawU);

        var origin = Assert.Single(field.OriginDeterminations);
        Assert.Equal("block", origin.OriginLabel);
        Assert.Equal(0.5, origin.DeterminationRate, 9);
        Assert.False(origin.Excluded);   // 0.5 << 0.95, and "block" != "b" regardless

        Assert.Equal(0.5, field.SmoothedM);
        Assert.Equal(0.5, field.SmoothedU);

        // Agreeing on "b" is worthless (indeed backwards) evidence here (m == u): the field must
        // be refused outright, not have bits computed and merely flagged.
        Assert.False(field.Usable);
        Assert.Null(field.AgreementBits);
        Assert.Null(field.DisagreementBits);
        Assert.NotNull(field.UnusableReason);
        Assert.Contains("DECREASES", field.UnusableReason, StringComparison.Ordinal);

        // Neither raw m nor raw u for "b" hits a 0/1 boundary, so this field is not also flagged
        // smoothing-dependent — the two guard rails are independent.
        Assert.False(field.SmoothingDependent);
        Assert.Empty(field.SmoothingSensitivity);
    }

    // ---- Field "c": zero same-entity observations, AND a small-N (n=1) CROSS-FIELD origin whose
    // rate happens to hit 1.0 -> must NOT exclude, since "block" != "c" (Critical Fix 1) ----

    [Fact]
    public void Calibrate_SmallNCrossFieldOrigin_NeverExcludes_RegardlessOfItsRate()
    {
        var field = Run().Fields.Single(f => f.FieldName == "c");

        Assert.Equal(0, field.SameEntityComparisons);
        Assert.Equal(0, field.SameEntityAgreements);
        Assert.Null(field.RawM);
        Assert.Null(field.SmoothedM);

        // Exactly one different-entity pair (r5,r10) has "c" populated on both sides, and it
        // agrees — a determination rate of 1.0 computed from a SINGLE observation, owned by
        // "block". Reported (a reader can see how thin this rate is), but "block" is not "c", so
        // it is NEVER excluded, no matter how extreme its rate — this is the exact shape of the
        // bug the reviewer reproduced with a "region" field, kept small here as a regression test.
        var origin = Assert.Single(field.OriginDeterminations);
        Assert.Equal("block", origin.OriginLabel);
        Assert.Equal(1, origin.Observations);
        Assert.Equal(1, origin.Agreements);
        Assert.Equal(1.0, origin.DeterminationRate, 9);
        Assert.False(origin.Excluded);

        Assert.Equal(1, field.DifferentEntityComparisons);
        Assert.Equal(1, field.DifferentEntityAgreements);
        Assert.Equal(0, field.DifferentEntityExcludedByDetermination);
        Assert.Equal(1.0, field.RawU);
        Assert.Equal(0.75, field.SmoothedU); // (1+0.5)/(1+1)

        // Bits need BOTH m and u; with m undefined, both must be null, not computed against a
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
            Assert.Equal(field.UnconditionedSameEntityComparisons, field.UnconditionedSameEntitySimilarityHistogram.Sum());
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
    // Determination-based exclusion, above threshold, restricted to a field's OWN origin
    // (Critical Fix 1). Fixture: TWO blocking fields ("dob", exact-value via DateOfBirth
    // semantic type; "ident", exact-value via the Identifier role) plus one Matchable-only field
    // ("region", never a blocking key) DELIBERATELY chosen so a CROSS-field origin ("dob")
    // measures >= 0.95 on it — reproducing the reviewer's finding that the previous version of
    // this rule excluded region's pairs anyway. exact-value keys are "{field}:{value}", so
    // ownership never has to be inferred — each field's key vocabulary is disjoint by
    // construction here, which is what makes the six candidate pairs below unambiguous:
    //
    //   r5/r6/r11 share "dob:19800101"   -> pairs (r5,r6) (r5,r11) (r6,r11), owned by "dob".
    //   r9/r10/r12 share "ident:z"        -> pairs (r9,r10) (r9,r12) (r10,r12), owned by "ident".
    //
    // Truth: r5/r6 = group GA (a true pair), r9/r10 = group GB (a true pair), r11 = group GC,
    // r12 = group GD (GC and GD each have no partner in this fixture, so they contribute no
    // same-entity pairs of their own — only cross pairs against GA/GB).
    //
    // region is EAST for r5/r6/r11 (everyone in the "dob" block), WEST for r9/r10, NORTH for r12.
    //
    // Field "dob": same-entity pairs, by owning origin: (r5,r6) owned by "dob" (dob AGREES, both
    // 19800101) -> bucket "dob": n=1, agree=1. (r9,r10) owned by "ident" (dob DISAGREES,
    // 19900505 vs 19910606) -> bucket "ident": n=1, agree=0. Different-entity pairs: origin "dob"
    // owns (r5,r11)/(r6,r11), BOTH agree on dob (all three share 19800101) -> 2/2 = 1.0 ->
    // EXCLUDED (it's dob's OWN origin). Origin "ident" owns (r9,r12)/(r10,r12), BOTH disagree on
    // dob -> 0/2 = 0.0 -> kept (not dob's own origin anyway). Excluding "dob"'s own-origin
    // bucket from BOTH sides leaves: conditioned m from the "ident" bucket only (n=1, agree=0,
    // raw m=0.0) and conditioned u from the "ident" bucket only (n=2, agree=0, raw u=0.0).
    // Smoothed: m=(0+0.5)/(1+1)=0.25, u=(0+0.5)/(2+1)=0.16667 -> m > u -> USABLE at +0.585 bits
    // (far below the +1.585 bits an unconditioned m=0.5 would have given — conditioning matters).
    //
    // Field "ident" is the mirror image, by the fixture's deliberate symmetry: same conditioned
    // numbers (m=0.25, u=0.16667, +0.585 bits), computed from the "dob"-origin's leftover bucket
    // instead of "ident"'s.
    //
    // Field "region" (NOT a blocking field, so NEVER its own origin): origin "dob" owns
    // (r5,r11)/(r6,r11) — region EAST/EAST for both -> determination 2/2 = 1.0. Origin "ident"
    // owns (r9,r12)/(r10,r12) — region WEST/NORTH, WEST/NORTH -> determination 0/2 = 0.0. Under
    // the FIXED rule, "dob" != "region", so its 1.0 rate excludes NOTHING: all 4 different-entity
    // pairs count, DiffCount=4, DiffAgree=2 (the two "dob"-owned pairs), raw u=0.5. Same-entity:
    // (r5,r6) region EAST/EAST agree (owned by "dob"), (r9,r10) region WEST/WEST agree (owned by
    // "ident") -> SameCount=2, SameAgree=2, raw m=1.0. Smoothed m=(2+0.5)/3=0.8333,
    // u=(2+0.5)/5=0.5 -> +0.737 agreement bits. (Under the BUGGY unrestricted rule this would have
    // excluded the two "dob"-owned different-entity pairs, leaving u=0/2=0.0 and inflating
    // agreement bits to +2.322 -- the exact defect this fixture is built to catch.)
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
            new ProfileField { Name = "region", SemanticType = SemanticFieldType.FirstName,
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

    private static EntityRecord ExclusionRec(string id, string dob, string ident, string region)
        => new()
        {
            Id = Guid.NewGuid(), ProjectId = Guid.Empty, SourceId = Guid.Empty, IngestBatchId = Guid.Empty,
            SourceRecordId = id,
            Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                { ["dob"] = dob, ["ident"] = ident, ["region"] = region },
            CreatedAt = DateTimeOffset.UnixEpoch
        };

    // All six ids are fit-half at fitFraction 0.5 (hand-verified, same ids as the fixture above).
    private static readonly IReadOnlyList<EntityRecord> ExclusionRecords =
    [
        ExclusionRec("r5", "19800101", "A", "EAST"),    // group GA
        ExclusionRec("r6", "19800101", "B", "EAST"),    // group GA (true pair with r5)
        ExclusionRec("r9", "19900505", "Z", "WEST"),    // group GB
        ExclusionRec("r10", "19910606", "Z", "WEST"),   // group GB (true pair with r9)
        ExclusionRec("r11", "19800101", "C", "EAST"),   // group GC
        ExclusionRec("r12", "19750303", "Z", "NORTH")   // group GD
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
    public void Calibrate_OwnOriginAboveThreshold_ExcludesFromBothMAndU_ConditionedOnSamePopulation()
    {
        var dob = RunExclusion().Fields.Single(f => f.FieldName == "dob");

        // Unconditioned m: both same-entity pairs, unfiltered -> 1 of 2 agree.
        Assert.Equal(2, dob.UnconditionedSameEntityComparisons);
        Assert.Equal(1, dob.UnconditionedSameEntityAgreements);
        Assert.Equal(0.5, dob.UnconditionedRawM);

        // "dob"'s own origin (2 obs, both agree, rate 1.0) is excluded from BOTH m and u; "ident"
        // (2 obs, 0 agree on dob, rate 0.0) is kept on both sides.
        var dobOrigin = dob.OriginDeterminations.Single(o => o.OriginLabel == "dob");
        Assert.Equal(2, dobOrigin.Observations);
        Assert.Equal(2, dobOrigin.Agreements);
        Assert.Equal(1.0, dobOrigin.DeterminationRate, 9);
        Assert.True(dobOrigin.Excluded);
        var identOrigin = dob.OriginDeterminations.Single(o => o.OriginLabel == "ident");
        Assert.Equal(0.0, identOrigin.DeterminationRate, 9);
        Assert.False(identOrigin.Excluded);

        // Conditioned m: only the "ident"-owned same-entity pair (r9,r10) remains, and it
        // disagrees on dob -> m = 0/1 = 0.0 (a raw boundary!). This is NOT the same as the
        // unconditioned m=0.5 above -- conditioning changed the number, not just the label.
        Assert.Equal(1, dob.SameEntityComparisons);
        Assert.Equal(0, dob.SameEntityAgreements);
        Assert.Equal(1, dob.SameEntityExcludedByDetermination);
        Assert.Equal(0.0, dob.RawM);

        Assert.Equal(2, dob.DifferentEntityComparisons);
        Assert.Equal(0, dob.DifferentEntityAgreements);
        Assert.Equal(2, dob.DifferentEntityExcludedByDetermination);
        Assert.Equal(0.0, dob.RawU);

        Assert.NotNull(dob.SmoothedM);
        Assert.Equal(0.25, dob.SmoothedM!.Value, 9);
        Assert.NotNull(dob.SmoothedU);
        Assert.Equal(1.0 / 6.0, dob.SmoothedU!.Value, 9);

        Assert.True(dob.Usable);
        Assert.NotNull(dob.AgreementBits);
        Assert.Equal(0.584962500721156, dob.AgreementBits!.Value, 9);
        Assert.NotNull(dob.DisagreementBits);
        Assert.Equal(-0.15200309344505, dob.DisagreementBits!.Value, 9);

        // raw m == 0 (a boundary now caught by the broadened SmoothingDependent check).
        Assert.True(dob.SmoothingDependent);
        Assert.NotEmpty(dob.SmoothingSensitivity);
    }

    [Fact]
    public void Calibrate_SecondFieldOwningADifferentBlock_ConditionsOnlyItsOwnPopulation()
    {
        // Mirror of the "dob" case: "ident" owns the OTHER block (r9/r10/r12 sharing "ident:z"),
        // so its own excluded observations come from THAT bucket, not "dob"'s -- the actual thing
        // under test is that origin attribution never confuses the two blocking fields.
        var ident = RunExclusion().Fields.Single(f => f.FieldName == "ident");

        var identOrigin = ident.OriginDeterminations.Single(o => o.OriginLabel == "ident");
        Assert.Equal(1.0, identOrigin.DeterminationRate, 9);
        Assert.True(identOrigin.Excluded);
        var dobOrigin = ident.OriginDeterminations.Single(o => o.OriginLabel == "dob");
        Assert.Equal(0.0, dobOrigin.DeterminationRate, 9);
        Assert.False(dobOrigin.Excluded);

        Assert.Equal(1, ident.SameEntityComparisons);
        Assert.Equal(0, ident.SameEntityAgreements);
        Assert.Equal(1, ident.SameEntityExcludedByDetermination);
        Assert.Equal(0.0, ident.RawM);

        Assert.Equal(2, ident.DifferentEntityComparisons);
        Assert.Equal(0, ident.DifferentEntityAgreements);
        Assert.Equal(2, ident.DifferentEntityExcludedByDetermination);
        Assert.Equal(0.0, ident.RawU);

        Assert.True(ident.Usable);
        Assert.NotNull(ident.AgreementBits);
        Assert.Equal(0.584962500721156, ident.AgreementBits!.Value, 9);
        Assert.NotNull(ident.DisagreementBits);
        Assert.Equal(-0.15200309344505, ident.DisagreementBits!.Value, 9);
        Assert.True(ident.SmoothingDependent);
    }

    // ---- THE regression test for Critical Fix 1: a cross-field origin measuring >= 0.95 on a
    // non-blocking field must not exclude anything from that field. ----

    [Fact]
    public void Calibrate_CrossFieldOriginAboveThreshold_NeverExcludesANonOwningField()
    {
        var region = RunExclusion().Fields.Single(f => f.FieldName == "region");

        // "dob" (a DIFFERENT field) measures 1.0 determination on "region" -- exactly the
        // reviewer's reproduction shape. It must be reported...
        var dobOrigin = region.OriginDeterminations.Single(o => o.OriginLabel == "dob");
        Assert.Equal(2, dobOrigin.Observations);
        Assert.Equal(2, dobOrigin.Agreements);
        Assert.Equal(1.0, dobOrigin.DeterminationRate, 9);
        // ...but NEVER excluded, because "dob" != "region": this is the field this fixture exists
        // to pin, replacing an earlier version of this test that passed for the wrong reason.
        Assert.False(dobOrigin.Excluded);

        var identOrigin = region.OriginDeterminations.Single(o => o.OriginLabel == "ident");
        Assert.Equal(0.0, identOrigin.DeterminationRate, 9);
        Assert.False(identOrigin.Excluded);

        // Conditioned == unconditioned in every respect: "region" is never itself an origin, so
        // nothing here can ever be excluded, however extreme any OTHER origin's rate measures.
        Assert.Equal(0, region.SameEntityExcludedByDetermination);
        Assert.Equal(0, region.DifferentEntityExcludedByDetermination);
        Assert.Equal(region.SameEntityComparisons, region.UnconditionedSameEntityComparisons);
        Assert.Equal(region.SameEntityAgreements, region.UnconditionedSameEntityAgreements);

        Assert.Equal(2, region.SameEntityComparisons);
        Assert.Equal(2, region.SameEntityAgreements);
        Assert.Equal(1.0, region.RawM);

        // ALL FOUR different-entity pairs count -- 2 from "dob" (both agree) + 2 from "ident"
        // (both disagree) -- not just the 2 "ident" would have left behind under the bug.
        Assert.Equal(4, region.DifferentEntityComparisons);
        Assert.Equal(2, region.DifferentEntityAgreements);
        Assert.Equal(0.5, region.RawU);

        Assert.NotNull(region.SmoothedM);
        Assert.Equal(2.5 / 3.0, region.SmoothedM!.Value, 9);
        Assert.NotNull(region.SmoothedU);
        Assert.Equal(0.5, region.SmoothedU!.Value, 9);

        Assert.True(region.Usable);
        Assert.NotNull(region.AgreementBits);
        // +0.737 bits -- the reviewer's own reproduction number for the correct, unexcluded
        // figure (their buggy-code figure was +2.322 bits, computed with the "dob" pairs wrongly
        // removed from u).
        Assert.Equal(0.736965594166206, region.AgreementBits!.Value, 9);
        Assert.NotNull(region.DisagreementBits);
        Assert.Equal(-1.58496250072116, region.DisagreementBits!.Value, 9);
    }

    // =====================================================================================
    // Determination-based exclusion, below threshold on a field's OWN origin: the SEC
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
    // full: DiffCount=13, DiffAgree=0 (raw u=0.0). Since the "company" origin's rate never
    // crosses the threshold, m is untouched too — conditioned == unconditioned throughout. This
    // is the falsifiable prediction the empirical-determination rule is for: a field can own
    // 100% of its candidates and still have its full un-excluded population survive, when sharing
    // its key does not actually force the field to agree.
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
    public void Calibrate_OwnOriginBelowThreshold_DoesNotExclude_FromEitherMOrU()
    {
        var result = NewService().Calibrate(PartialSignalRecords, PartialSignalProfile(), PartialSignalTruth);

        // One key ("name:corp") over all 6 records: C(6,2) = 15 candidate pairs.
        Assert.Equal(15, result.CandidatePairsEmitted);
        Assert.Equal(2, result.LabeledSameEntityPairs);
        Assert.Equal(13, result.LabeledDifferentEntityPairs);
        Assert.Equal(0, result.UnattributableOwnerCandidatePairs);

        var company = Assert.Single(result.Fields);

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

        Assert.Equal(0, company.SameEntityExcludedByDetermination);
        Assert.Equal(0, company.DifferentEntityExcludedByDetermination);
        Assert.Equal(company.SameEntityComparisons, company.UnconditionedSameEntityComparisons);
        Assert.Equal(company.SameEntityAgreements, company.UnconditionedSameEntityAgreements);

        Assert.Equal(2, company.SameEntityComparisons);
        Assert.Equal(2, company.SameEntityAgreements);
        Assert.Equal(1.0, company.RawM);

        Assert.Equal(13, company.DifferentEntityComparisons);
        Assert.Equal(0, company.DifferentEntityAgreements);
        Assert.Equal(0.0, company.RawU);

        // Both raw m == 1 and raw u == 0 here too (a coincidence of this small fixture, not a
        // requirement of the below-threshold behavior under test) -- still usable, still reports
        // smoothing sensitivity, exactly like field "a" above.
        Assert.True(company.Usable);
        Assert.True(company.SmoothingDependent);
        Assert.False(company.SmoothedOrderingDiffersFromRaw);
        Assert.NotEmpty(company.SmoothingSensitivity);
    }

    // =====================================================================================
    // Important 3: the smoothing boundary flag must catch ALL FOUR raw 0/1 directions, and a
    // separate flag must catch smoothing flipping which of m/u is larger even when neither raw
    // value sits at a boundary in a way the FIRST flag would catch on its own.
    //
    // Fixture: one blocking field "key" used ONLY to build seven separate two-record blocks (so
    // each block contributes EXACTLY one candidate pair, fully controllable), and one
    // Matchable-only field "flag" (never a blocking key, so never self-excluded -- isolating this
    // fixture from the determination mechanism entirely). Block K1 is a same-entity pair with
    // "flag" AGREEING (the one m observation). Blocks K2..K6 are five different-entity pairs with
    // "flag" AGREEING; block K7 is one different-entity pair with "flag" DISAGREEING. So m = 1/1
    // (raw m=1.0, itself a boundary) and u = 5/6 (raw u=0.8333, NOT a boundary).
    //
    // Smoothed: m=(1+0.5)/(1+1)=0.75; u=(5+0.5)/(6+1)=5.5/7=0.785714. Raw order says m(1.0) >
    // u(0.8333); smoothed order says u(0.785714) > m(0.75) -- FLIPPED. Because Usable is decided
    // from the smoothed values, this field is UNUSABLE even though its raw data says m clearly
    // exceeds u. SmoothingDependent also fires here (raw m==1 is one of the four boundary cases),
    // but the ordering-flip flag is a logically separate computation and is asserted directly.
    // =====================================================================================

    private static MatchingProfile OrderingFlipProfile() => new()
    {
        ContentType = "person",
        Fields =
        [
            new ProfileField { Name = "key", SemanticType = SemanticFieldType.SourceIdentifier,
                Roles = FieldRole.Blocking | FieldRole.Identifier, SimilarityEvaluator = "exact", Weight = 1.0 },
            new ProfileField { Name = "flag", SemanticType = SemanticFieldType.FirstName,
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

    private static EntityRecord OrderingFlipRec(string id, string key, string flag)
        => new()
        {
            Id = Guid.NewGuid(), ProjectId = Guid.Empty, SourceId = Guid.Empty, IngestBatchId = Guid.Empty,
            SourceRecordId = id,
            Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["key"] = key, ["flag"] = flag },
            CreatedAt = DateTimeOffset.UnixEpoch
        };

    // Every id below is hand-verified fit-half at fitFraction 0.5 (SHA-256 of the id, same method
    // as IsFitHalf tests above).
    private static readonly IReadOnlyList<EntityRecord> OrderingFlipRecords =
    [
        // Block K1: SAME-entity pair, flag AGREES (the one m observation).
        OrderingFlipRec("r5", "K1", "X"),
        OrderingFlipRec("r6", "K1", "X"),
        // Blocks K2..K6: five DIFFERENT-entity pairs, flag AGREES in each.
        OrderingFlipRec("r9", "K2", "Y"),
        OrderingFlipRec("r10", "K2", "Y"),
        OrderingFlipRec("r11", "K3", "Y"),
        OrderingFlipRec("r12", "K3", "Y"),
        OrderingFlipRec("r15", "K4", "Y"),
        OrderingFlipRec("r16", "K4", "Y"),
        OrderingFlipRec("r19", "K5", "Y"),
        OrderingFlipRec("r21", "K5", "Y"),
        OrderingFlipRec("r24", "K6", "Y"),
        OrderingFlipRec("r25", "K6", "Y"),
        // Block K7: one DIFFERENT-entity pair, flag DISAGREES.
        OrderingFlipRec("q4", "K7", "P"),
        OrderingFlipRec("q5", "K7", "Q")
    ];

    private static readonly Dictionary<string, string> OrderingFlipTruth = new()
    {
        ["r5"] = "SAME", ["r6"] = "SAME",
        ["r9"] = "D1", ["r10"] = "D2",
        ["r11"] = "D3", ["r12"] = "D4",
        ["r15"] = "D5", ["r16"] = "D6",
        ["r19"] = "D7", ["r21"] = "D8",
        ["r24"] = "D9", ["r25"] = "D10",
        ["q4"] = "D11", ["q5"] = "D12"
    };

    [Fact]
    public void Calibrate_SmoothingFlipsOrdering_IsFlaggedIndependentlyOfSmoothingDependent()
    {
        var result = NewService().Calibrate(OrderingFlipRecords, OrderingFlipProfile(), OrderingFlipTruth);

        Assert.Equal(1, result.LabeledSameEntityPairs);
        Assert.Equal(6, result.LabeledDifferentEntityPairs);

        var flag = Assert.Single(result.Fields);
        Assert.Equal(1, flag.SameEntityComparisons);
        Assert.Equal(1, flag.SameEntityAgreements);
        Assert.Equal(1.0, flag.RawM);
        Assert.Equal(6, flag.DifferentEntityComparisons);
        Assert.Equal(5, flag.DifferentEntityAgreements);
        Assert.Equal(5.0 / 6.0, flag.RawU!.Value, 9);

        Assert.Equal(0.75, flag.SmoothedM!.Value, 9);
        Assert.Equal(5.5 / 7.0, flag.SmoothedU!.Value, 9);

        // Raw order: m (1.0) > u (0.8333). Smoothed order: u (0.785714) > m (0.75). Flipped.
        Assert.True(flag.SmoothedOrderingDiffersFromRaw);

        // raw m == 1 is itself one of the four boundary cases -- SmoothingDependent fires too,
        // but that is a SEPARATE computation from the ordering-flip flag, not a prerequisite for
        // it (see the fixture doc above).
        Assert.True(flag.SmoothingDependent);

        // Usable is decided from the SMOOTHED values (0.75 <= 0.785714), so despite raw m clearly
        // exceeding raw u, this field is UNUSABLE -- the smoothing constant, not the data alone,
        // decided it.
        Assert.False(flag.Usable);
        Assert.Null(flag.AgreementBits);
        Assert.Null(flag.DisagreementBits);
    }
}
