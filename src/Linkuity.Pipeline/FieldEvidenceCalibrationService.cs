using System.Security.Cryptography;
using System.Text;
using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;

namespace Linkuity.Pipeline;

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
/// were observations, <see cref="SmoothedM"/>/<see cref="SmoothedU"/> apply a Laplace ("add
/// half") continuity correction — <c>(agreements + 0.5) / (comparisons + 1)</c> — so a raw 0 or 1
/// (both of which <see cref="FieldEvidence"/> refuses outright) becomes a usable, strictly-open
/// probability that still converges to the raw rate as the observation count grows. The
/// correction is applied EXPLICITLY and reported alongside the raw rate specifically so nobody
/// downstream mistakes the smoothed value for a measurement free of assumptions.
/// </para>
/// <para>
/// <see cref="AgreementBits"/>/<see cref="DisagreementBits"/> are computed from the smoothed
/// probabilities, not from a constructed <see cref="FieldEvidence"/>: that type throws on
/// <c>m &lt;= u</c>, which is exactly the case this instrument must be able to REPORT rather
/// than crash on (see <see cref="EvidenceInverted"/>). Applying the numbers into a profile's
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
    double? AgreementBits,
    double? DisagreementBits,
    /// <summary>True when SmoothedM &lt;= SmoothedU: agreeing on this field would be evidence
    /// AGAINST a match. Almost always a misconfigured field or evaluator, not a real finding —
    /// callers must surface this prominently rather than let it slide by as one more row.</summary>
    bool EvidenceInverted,
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
/// </summary>
public sealed class FieldEvidenceCalibrationService
{
    private const int HistogramBuckets = 10;
    private const double AgreementEpsilon = 1e-9;

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

    private static FieldCalibrationRow BuildRow(string name, FieldAccumulator acc)
    {
        double? rawM = acc.SameCount > 0 ? (double)acc.SameAgree / acc.SameCount : null;
        double? rawU = acc.DiffCount > 0 ? (double)acc.DiffAgree / acc.DiffCount : null;

        // Laplace continuity correction: undefined (null) with zero observations, otherwise
        // always strictly inside (0,1) no matter what the raw rate was — see the class doc.
        double? smoothedM = acc.SameCount > 0 ? (acc.SameAgree + 0.5) / (acc.SameCount + 1) : null;
        double? smoothedU = acc.DiffCount > 0 ? (acc.DiffAgree + 0.5) / (acc.DiffCount + 1) : null;

        double? agreementBits = null, disagreementBits = null;
        var inverted = false;
        if (smoothedM is { } m && smoothedU is { } u)
        {
            agreementBits = Math.Log2(m / u);
            disagreementBits = Math.Log2((1 - m) / (1 - u));
            inverted = m <= u;
        }

        return new FieldCalibrationRow(
            name,
            acc.SameCount, acc.SameAgree, rawM, smoothedM,
            acc.DiffCount, acc.DiffAgree, rawU, smoothedU,
            agreementBits, disagreementBits, inverted,
            acc.SameHistogram, acc.DiffHistogram);
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
