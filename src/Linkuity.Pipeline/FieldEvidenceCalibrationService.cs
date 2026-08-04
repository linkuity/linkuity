using System.Security.Cryptography;
using System.Text;
using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;

namespace Linkuity.Pipeline;

/// <summary>
/// One field's m/u under one smoothing constant (see <see cref="FieldCalibrationRow.SmoothingSensitivity"/>).
/// Exists so a reader can see, side by side, how much of a SMOOTHING-DEPENDENT field's bits come
/// from data versus from the continuity-correction constant.
/// </summary>
public sealed record SmoothingVariant(
    double Alpha,
    double SmoothedM,
    double SmoothedU,
    double? AgreementBits,
    double? DisagreementBits);

/// <summary>
/// One matchable field's calibration: the two Fellegi-Sunter probabilities the evidence scorer
/// needs (see <see cref="FieldEvidence"/>), estimated from labelled candidate pairs, plus every
/// number a reader needs to judge whether the estimate is trustworthy.
/// <para>
/// "Agree" means exact agreement — <c>SimilaritySignal.Value == 1.0</c> on a
/// <see cref="ComparisonOutcome.Compared"/> signal — because that is what
/// <c>FieldEvidence.EvidenceFor</c> treats as full agreement. A fuzzy evaluator that rarely
/// returns exactly 1.0 will show that as a small comparison count here; it must not be hidden
/// behind an average similarity.
/// </para>
/// <para>
/// <see cref="RawM"/>/<see cref="RawU"/> are the unadjusted agreement rates and are null when
/// there were zero observations to compute them from — never a fabricated number. When there
/// were observations, <see cref="SmoothedM"/>/<see cref="SmoothedU"/> apply a continuity
/// correction — <c>(agreements + alpha) / (comparisons + 2*alpha)</c> at the primary
/// <c>alpha = 0.5</c> — so a raw 0 or 1 (both of which <see cref="FieldEvidence"/> refuses
/// outright) becomes a usable, strictly-open probability that still converges to the raw rate as
/// the observation count grows. The correction is applied EXPLICITLY and reported alongside the
/// raw rate specifically so nobody downstream mistakes the smoothed value for a measurement free
/// of assumptions.
/// </para>
/// <para>
/// <see cref="AgreementBits"/>/<see cref="DisagreementBits"/> are computed from the PRIMARY
/// smoothed probabilities, not from a constructed <see cref="FieldEvidence"/>: that type throws
/// on <c>m &lt;= u</c>, and this instrument must be able to REPORT that case rather than crash on
/// it (see <see cref="Usable"/>). Applying the numbers into a profile's
/// <see cref="FieldEvidence"/> is deliberately a separate, later step.
/// </para>
/// </summary>
public sealed record FieldCalibrationRow(
    string FieldName,
    long SameEntityComparisons,
    long SameEntityAgreements,
    double? RawM,
    double? SmoothedM,
    long DifferentEntityComparisons,
    long DifferentEntityAgreements,
    double? RawU,
    double? SmoothedU,
    /// <summary>Null whenever <see cref="Usable"/> is false or there is insufficient data on
    /// either side — this instrument NEVER emits a bits pair a caller could mistake for a usable
    /// parameter in either of those cases.</summary>
    double? AgreementBits,
    double? DisagreementBits,
    /// <summary>
    /// False exactly when SmoothedM &lt;= SmoothedU — which is exactly the condition under which
    /// agreement bits &lt;= 0 &lt;= disagreement bits, i.e. evidence DECREASES as similarity
    /// increases. When false, <see cref="AgreementBits"/>/<see cref="DisagreementBits"/> are both
    /// null: this instrument refuses to emit parameters for a field whose evidence runs backwards,
    /// rather than let a caller apply them and get worse merges the more two records agree.
    /// Deciding what to DO about an unusable field (drop it, reblock, recalibrate) is a separate,
    /// later judgement — this only refuses to manufacture a number for it.
    /// </summary>
    bool Usable,
    /// <summary>Human-readable reason, populated iff <c>!Usable</c>.</summary>
    string? UnusableReason,
    /// <summary>
    /// True when the RAW (unsmoothed) estimate is degenerate in a direction that would send bits
    /// to +/-infinity without the continuity correction: raw m == 1 (disagreement bits would be
    /// log2(0/x) = -infinity) or raw u == 0 (agreement bits would be log2(m/0) = +infinity). When
    /// true, the reported <see cref="AgreementBits"/>/<see cref="DisagreementBits"/> rest entirely
    /// on the smoothing constant rather than on any observed disagreement/coincidence, and
    /// <see cref="SmoothingSensitivity"/> shows how much they move under a different constant.
    /// </summary>
    bool SmoothingDependent,
    /// <summary>
    /// Populated iff <see cref="SmoothingDependent"/> and <see cref="Usable"/>: the same field's
    /// m/u and bits recomputed under two or more smoothing constants (0.5, the primary value used
    /// above, and 1.0, the classic Laplace add-one rule), so the reader can see at a glance how
    /// much of the number is measurement versus modelling choice. Empty otherwise.
    /// </summary>
    IReadOnlyList<SmoothingVariant> SmoothingSensitivity,
    /// <summary>10 buckets over [0,1]: bucket i is [i/10, (i+1)/10), except the last bucket,
    /// which is closed on both ends ([0.9, 1.0]). Same-entity (true-pair) observations only.</summary>
    IReadOnlyList<long> SameEntitySimilarityHistogram,
    /// <summary>Same bucketing as <see cref="SameEntitySimilarityHistogram"/>, over
    /// different-entity (non-match) observations.</summary>
    IReadOnlyList<long> DifferentEntitySimilarityHistogram);

/// <summary>Corpus-level counts around a calibration run, so the per-field table can be read in
/// context rather than as bare numbers.</summary>
public sealed record FieldEvidenceCalibrationResult(
    int TotalRecords,
    int FitRecords,
    int EvalRecords,
    double FitFraction,
    long CandidateOccurrences,
    long CandidatePairsEmitted,
    long LabeledSameEntityPairs,
    long LabeledDifferentEntityPairs,
    long UnlabeledCandidatePairs,
    IReadOnlyList<FieldCalibrationRow> Fields);

/// <summary>
/// Estimates m (same-entity agreement) and u (chance agreement) per matchable field, for the
/// evidence scorer's <see cref="FieldEvidence"/> parameters — see
/// docs/superpowers/specs (evidence-calibration). Three requirements shape every choice here:
/// <list type="number">
/// <item><description>A deterministic, hash-of-id held-out split: the fit half is used for
/// estimation, the eval half is never touched by this service at all (see
/// <see cref="IsFitHalf"/>) — not merely "not used for the final number", but not even loaded
/// into the blocking index, so it structurally cannot leak in.</description></item>
/// <item><description>u is estimated over candidate pairs BLOCKING actually produces, not over
/// uniformly random pairs — the same defect random-pair calibration has everywhere it is tried.
/// This service reuses <see cref="CorpusAuditService.BuildIndex"/> and
/// <see cref="CorpusAuditService.ForEachCandidatePair"/> — the exact walk `match corpus audit`
/// runs — rather than re-implementing a second, potentially-divergent candidate walk.</description></item>
/// <item><description>"Agree" is defined once (similarity == 1.0 on a Compared signal, see the
/// <see cref="FieldCalibrationRow"/> doc) and every count behind an estimate is reported, not
/// just the ratio.</description></item>
/// </list>
/// <para>
/// Scored with a SINGLE direction per candidate pair (<c>similarity.Evaluate(left, right, ...)</c>
/// in the index's own (lower, higher) order), unlike <see cref="CorpusAuditService.ScorePair"/>'s
/// max-of-both-directions: this service measures what one evaluator call returns for one field,
/// and every shipped evaluator (exact/jaccard/canonical-jaccard/fuzzy/numeric/date) is a symmetric
/// function of its two string arguments, so direction does not change the value they return.
/// </para>
/// <para>
/// A field can come back UNUSABLE (<see cref="FieldCalibrationRow.Usable"/> false): m &lt;= u
/// means agreeing on the field is evidence AGAINST a match, which is almost always a sign the
/// field (or its interaction with blocking — a field also used as a blocking key will see its u
/// inflated by construction, since most candidates already share it) is misconfigured, not a real
/// finding to encode. This service refuses to emit AgreementBits/DisagreementBits for such a
/// field rather than pick a policy (exclude it, clamp it, floor it) — that decision depends on
/// blocking and belongs to whoever applies these numbers, not to this measurement.
/// </para>
/// </summary>
public sealed class FieldEvidenceCalibrationService
{
    private const int HistogramBuckets = 10;
    private const double AgreementEpsilon = 1e-9;

    /// <summary>The constant used for <see cref="FieldCalibrationRow.SmoothedM"/>/<c>SmoothedU</c>
    /// and every field's AgreementBits/DisagreementBits. 0.5 (a Jeffreys-style "add half" prior)
    /// rather than 1.0 (classic Laplace): it pulls a degenerate raw rate less aggressively toward
    /// 0.5, so the reported number tracks the data more closely while still being strictly open.</summary>
    private const double PrimarySmoothingAlpha = 0.5;

    /// <summary>Second constant shown for SMOOTHING-DEPENDENT fields only, so the reader can see
    /// how far the primary number would move under a materially different (and equally
    /// defensible) choice — classic Laplace add-one.</summary>
    private const double SecondarySmoothingAlpha = 1.0;

    private readonly IStrategyRegistry _registry;

    public FieldEvidenceCalibrationService(IStrategyRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public FieldEvidenceCalibrationResult Calibrate(
        IReadOnlyList<EntityRecord> records,
        MatchingProfile profile,
        IReadOnlyDictionary<string, string> groundTruth,
        int? maxBlockSize = null,
        double fitFraction = 0.5,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(groundTruth);

        if (double.IsNaN(fitFraction) || fitFraction <= 0 || fitFraction >= 1)
            throw new ArgumentOutOfRangeException(nameof(fitFraction), fitFraction,
                "fitFraction must be strictly between 0 and 1.");

        // Same two strategies ScoringAuditService requires, for the same reason: per-field
        // breakdowns assume field-named signals (similarityStrategy) evaluated over records this
        // service normalized itself the same way blocking will (normalizationStrategy). This
        // instrument never scores or clusters, so it does not need threshold/union-find guards.
        if (!string.Equals(profile.NormalizationStrategy, "identity", StringComparison.Ordinal))
            throw new ArgumentException(
                $"Calibration requires normalizationStrategy 'identity' (profile has " +
                $"'{profile.NormalizationStrategy}').");
        if (!string.Equals(profile.SimilarityStrategy, "field-weighted", StringComparison.Ordinal))
            throw new ArgumentException(
                $"Calibration requires similarityStrategy 'field-weighted' (profile has " +
                $"'{profile.SimilarityStrategy}'): per-field m/u estimation assumes field-named signals.");

        var duplicate = records
            .GroupBy(r => r.SourceRecordId, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Duplicate SourceRecordId in input: '{duplicate.Key}'.");

        // Physical partition, not a filter applied after the fact: the eval half's records are
        // never added to `fit`, so they never reach normalization, the blocking index, or the
        // candidate walk below. That is what "never informs the parameters" means structurally,
        // not just as a promise about which numbers get reported.
        var fit = new List<EntityRecord>();
        var evalCount = 0;
        foreach (var record in records)
        {
            if (IsFitHalf(record.SourceRecordId, fitFraction)) fit.Add(record);
            else evalCount++;
        }

        var normalization = _registry.Normalization[profile.NormalizationStrategy];
        var similarity = _registry.Similarity[profile.SimilarityStrategy];

        var normalized = fit.Select(r => normalization.Normalize(r, profile)).ToArray();
        var index = CorpusAuditService.BuildIndex(fit, profile, _registry, ct);
        var effectiveMax = maxBlockSize ?? profile.MaxBlockSize;

        var matchableFields = profile.Fields
            .Where(f => f.Roles.HasFlag(FieldRole.Matchable))
            .Select(f => f.Name)
            .ToList();
        var accumulators = matchableFields.ToDictionary(
            n => n, _ => new FieldAccumulator(HistogramBuckets), StringComparer.Ordinal);

        long emitted = 0, labeledSame = 0, labeledDifferent = 0, unlabeled = 0;

        var occurrences = CorpusAuditService.ForEachCandidatePair(index, effectiveMax, (l, r) =>
        {
            emitted++;

            if (!groundTruth.TryGetValue(fit[l].SourceRecordId, out var leftLabel) ||
                !groundTruth.TryGetValue(fit[r].SourceRecordId, out var rightLabel))
            {
                unlabeled++;
                return;
            }

            var sameEntity = string.Equals(leftLabel, rightLabel, StringComparison.Ordinal);
            if (sameEntity) labeledSame++; else labeledDifferent++;

            var signals = similarity.Evaluate(normalized[l], normalized[r], profile);
            foreach (var signal in signals)
            {
                // Only Compared observations are agreement/disagreement evidence: Missing/
                // NotComparable signals contribute zero to the evidence scorer regardless of m/u
                // (EvidenceScoringStrategy.Score), so calibrating on them would estimate a
                // probability for an event the scorer never prices.
                if (signal.Outcome != ComparisonOutcome.Compared) continue;
                if (accumulators.TryGetValue(signal.Name, out var accumulator))
                    accumulator.Record(sameEntity, signal.Value);
            }
        }, ct);

        var rows = matchableFields.Select(name => BuildRow(name, accumulators[name])).ToList();

        return new FieldEvidenceCalibrationResult(
            records.Count, fit.Count, evalCount, fitFraction,
            occurrences, emitted, labeledSame, labeledDifferent, unlabeled, rows);
    }

    /// <summary>
    /// Deterministic fit/eval assignment from a SHA-256 of the record id's UTF-8 bytes, composed
    /// into a ulong byte-by-byte (never via <see cref="BitConverter"/>, whose endianness depends
    /// on the host architecture) so the split is identical across runs AND machines — the
    /// property the task calls out explicitly, and the one plain <c>GetHashCode</c> cannot offer
    /// (randomized per process since .NET Core). Independent of arrival order: the same id
    /// always lands on the same side no matter where it sits in the input file.
    /// </summary>
    internal static bool IsFitHalf(string sourceRecordId, double fitFraction)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sourceRecordId));
        ulong v = 0;
        for (var i = 0; i < 8; i++) v = (v << 8) | hash[i];
        var fraction = v / (double)ulong.MaxValue;
        return fraction < fitFraction;
    }

    /// <summary>Additive ("continuity correction") smoothing: symmetric in the sense that both
    /// classes (agree/disagree) get <paramref name="alpha"/> phantom observations, so the
    /// estimate always sits strictly inside (0,1) yet converges to the raw rate as the real
    /// observation count grows. Null when there is no data at all — smoothing a zero-observation
    /// count would report a number for a field nobody measured anything about.</summary>
    private static double? SmoothedRate(long agree, long count, double alpha)
        => count > 0 ? (agree + alpha) / (count + 2 * alpha) : null;

    private static FieldCalibrationRow BuildRow(string name, FieldAccumulator acc)
    {
        double? rawM = acc.SameCount > 0 ? (double)acc.SameAgree / acc.SameCount : null;
        double? rawU = acc.DiffCount > 0 ? (double)acc.DiffAgree / acc.DiffCount : null;

        var smoothedM = SmoothedRate(acc.SameAgree, acc.SameCount, PrimarySmoothingAlpha);
        var smoothedU = SmoothedRate(acc.DiffAgree, acc.DiffCount, PrimarySmoothingAlpha);

        double? agreementBits = null, disagreementBits = null;
        var usable = true;
        string? unusableReason = null;

        if (smoothedM is { } m && smoothedU is { } u)
        {
            if (m <= u)
            {
                // m <= u is EXACTLY the condition "agreement bits <= 0 <= disagreement bits" —
                // Math.Log2(m/u) <= 0 whenever m <= u, and Math.Log2((1-m)/(1-u)) >= 0 whenever
                // m <= u — i.e. evidence from this field DECREASES as similarity increases. Stated
                // that way, not as a bare inequality, because the inequality alone reads as a
                // technicality and the consequence is what makes it dangerous to use.
                usable = false;
                unusableReason =
                    "m <= u: evidence from this field DECREASES as similarity increases (full " +
                    "agreement is worth fewer bits than full disagreement). Refusing to emit " +
                    "AgreementBits/DisagreementBits rather than encode a parameter that rewards " +
                    "records looking LESS alike. Deciding what to do about this field (drop it, " +
                    "recalibrate under different blocking, accept it) is a separate judgement.";
            }
            else
            {
                agreementBits = Math.Log2(m / u);
                disagreementBits = Math.Log2((1 - m) / (1 - u));
            }
        }

        // Degenerate in the direction that would send a bit to +/-infinity without the
        // continuity correction — see the SmoothingDependent doc on FieldCalibrationRow.
        var smoothingDependent = rawM is 1.0 || rawU is 0.0;

        IReadOnlyList<SmoothingVariant> sensitivity = [];
        if (smoothingDependent && usable)
        {
            sensitivity = new[] { PrimarySmoothingAlpha, SecondarySmoothingAlpha }
                .Select(alpha => BuildVariant(acc, alpha))
                .ToList();
        }

        return new FieldCalibrationRow(
            name,
            acc.SameCount, acc.SameAgree, rawM, smoothedM,
            acc.DiffCount, acc.DiffAgree, rawU, smoothedU,
            agreementBits, disagreementBits, usable, unusableReason,
            smoothingDependent, sensitivity,
            acc.SameHistogram, acc.DiffHistogram);
    }

    private static SmoothingVariant BuildVariant(FieldAccumulator acc, double alpha)
    {
        // Guarded by the smoothingDependent+usable check at the call site, which already implies
        // both sides have at least one observation (SameCount>0 to have a raw m, DiffCount>0 to
        // have a raw u), so these are never null here.
        var m = SmoothedRate(acc.SameAgree, acc.SameCount, alpha)!.Value;
        var u = SmoothedRate(acc.DiffAgree, acc.DiffCount, alpha)!.Value;
        double? agreementBits = null, disagreementBits = null;
        if (m > u)
        {
            agreementBits = Math.Log2(m / u);
            disagreementBits = Math.Log2((1 - m) / (1 - u));
        }
        return new SmoothingVariant(alpha, m, u, agreementBits, disagreementBits);
    }

    /// <summary>Per-field running tallies over one candidate walk. Mutable by design — one
    /// instance accumulates every observation for its field across the whole corpus walk, which
    /// a record type would make needlessly awkward to update in place.</summary>
    private sealed class FieldAccumulator(int buckets)
    {
        private readonly long[] _sameHistogram = new long[buckets];
        private readonly long[] _diffHistogram = new long[buckets];

        public long SameCount { get; private set; }
        public long SameAgree { get; private set; }
        public long DiffCount { get; private set; }
        public long DiffAgree { get; private set; }
        public IReadOnlyList<long> SameHistogram => _sameHistogram;
        public IReadOnlyList<long> DiffHistogram => _diffHistogram;

        public void Record(bool sameEntity, double value)
        {
            var clamped = Math.Clamp(value, 0.0, 1.0);
            var bucket = Math.Min(buckets - 1, (int)(clamped * buckets));
            var agree = clamped >= 1.0 - AgreementEpsilon;

            if (sameEntity)
            {
                SameCount++;
                _sameHistogram[bucket]++;
                if (agree) SameAgree++;
            }
            else
            {
                DiffCount++;
                _diffHistogram[bucket]++;
                if (agree) DiffAgree++;
            }
        }
    }
}
