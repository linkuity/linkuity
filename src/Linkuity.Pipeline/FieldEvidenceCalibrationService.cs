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
/// One origin's contribution to a field's DIFFERENT-entity population, and whether it was excluded
/// — see "EMPIRICAL DETERMINATION" in the <see cref="FieldEvidenceCalibrationService"/> class doc.
/// <see cref="OriginLabel"/> is the blocking-role field the owning key was attributed to, or
/// <see cref="FieldEvidenceCalibrationService.UnattributableOriginLabel"/> when it could not be
/// (see <see cref="FieldEvidenceCalibrationService.BuildKeyFieldAttribution"/>).
/// <para>
/// <see cref="Excluded"/> is true ONLY when this row is the field's OWN origin (OriginLabel equals
/// the field being measured) AND its rate is at/above the determination threshold — a
/// cross-field origin's rate is reported here as a diagnostic (it tells a reader how correlated
/// two fields' blocking behaviour is) but NEVER excludes anything, no matter how high it measures.
/// </para>
/// <para>
/// <see cref="Observations"/> is reported alongside the rate for exactly the reason the task
/// called out: a rate computed from a handful of pairs is not the same kind of number as one
/// computed from thousands, and a reader must be able to tell them apart at a glance.
/// </para>
/// </summary>
public sealed record OriginDetermination(
    string OriginLabel,
    long Observations,
    long Agreements,
    double DeterminationRate,
    bool Excluded);

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
/// CONDITIONED VS UNCONDITIONED m: <see cref="SameEntityComparisons"/>/<see cref="SameEntityAgreements"/>/
/// <see cref="RawM"/>/<see cref="SmoothedM"/> are CONDITIONED on the same population u is estimated
/// from — same-entity pairs owned by this field's own excluded origin (see
/// <see cref="SameEntityExcludedByDetermination"/>) are removed from m for the identical reason
/// they are removed from u: <c>log2(m/u)</c> must be a likelihood ratio for ONE population, not m
/// over "all candidates" divided by u over "candidates minus the self-forcing ones". The
/// <c>Unconditioned*</c> properties report the SAME field's m over every same-entity observation
/// regardless of origin, so the difference conditioning makes is visible rather than hidden. When
/// the field is not itself a blocking-role field, or its own origin never crosses the
/// determination threshold, conditioned and unconditioned are identical — nothing was excluded.
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
/// (conditioned) smoothed probabilities, not from a constructed <see cref="FieldEvidence"/>: that
/// type throws on <c>m &lt;= u</c>, and this instrument must be able to REPORT that case rather
/// than crash on it (see <see cref="Usable"/>). Applying the numbers into a profile's
/// <see cref="FieldEvidence"/> is deliberately a separate, later step.
/// </para>
/// </summary>
public sealed record FieldCalibrationRow(
    string FieldName,
    /// <summary>CONDITIONED same-entity Compared observation count — see the class doc's
    /// "CONDITIONED VS UNCONDITIONED m" section.</summary>
    long SameEntityComparisons,
    long SameEntityAgreements,
    double? RawM,
    double? SmoothedM,
    /// <summary>Same-entity pairs excluded from <see cref="SameEntityComparisons"/> because they
    /// were owned by this field's own self-determining origin — the exact origin excluded from
    /// <see cref="DifferentEntityExcludedByDetermination"/>, applied to m for the same reason.
    /// Zero whenever nothing was excluded from u either.</summary>
    long SameEntityExcludedByDetermination,
    /// <summary>UNCONDITIONED same-entity Compared observation count — every same-entity
    /// observation regardless of which origin owns the pair. Reported for comparison with the
    /// conditioned <see cref="SameEntityComparisons"/> above; NOT used for AgreementBits/
    /// DisagreementBits/Usable.</summary>
    long UnconditionedSameEntityComparisons,
    long UnconditionedSameEntityAgreements,
    double? UnconditionedRawM,
    double? UnconditionedSmoothedM,
    /// <summary>Different-entity Compared observations actually used to estimate u — i.e. AFTER
    /// excluding pairs owned by THIS FIELD'S OWN origin, when that origin's empirically measured
    /// determination rate on this field is at or above the threshold (see
    /// <see cref="DifferentEntityExcludedByDetermination"/> and <see cref="OriginDeterminations"/>).
    /// A cross-field origin, however high its rate measures, never removes anything here.</summary>
    long DifferentEntityComparisons,
    long DifferentEntityAgreements,
    double? RawU,
    double? SmoothedU,
    /// <summary>
    /// Different-entity candidate pairs excluded from <see cref="DifferentEntityComparisons"/> /
    /// ChanceAgreement because they were owned by THIS FIELD'S OWN origin and that origin's
    /// measured agreement rate on this field was at or above the determination threshold — see
    /// <see cref="OriginDeterminations"/> for the per-origin breakdown that produced this total.
    /// Zero for a field with no self-forcing origin, INCLUDING a field whose own origin's rate is
    /// below threshold and every field that is not itself a blocking-role field at all.
    /// </summary>
    long DifferentEntityExcludedByDetermination,
    /// <summary>
    /// Every origin (blocking-role field, or the unattributable bucket) that owned at least one of
    /// this field's different-entity candidate observations, with its own observation count,
    /// agreement count, and empirically measured determination rate on THIS field. Only the row
    /// whose OriginLabel equals this field's own name can ever have <c>Excluded == true</c> — every
    /// other row is a diagnostic (how much does sharing THAT origin's key correlate with agreeing
    /// on THIS field?) that never decides anything, no matter how high its rate measures.
    /// </summary>
    IReadOnlyList<OriginDetermination> OriginDeterminations,
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
    /// True when the CONDITIONED raw (unsmoothed) estimate is degenerate — raw m or raw u equal to
    /// EITHER 0 or 1 — a direction that would send a bit to +/-infinity without the continuity
    /// correction (raw m==1 or raw u==0 send agreement/disagreement bits to -/+infinity; raw m==0
    /// or raw u==1 do the same in the other direction). When true, the reported
    /// <see cref="AgreementBits"/>/<see cref="DisagreementBits"/> rest entirely on the smoothing
    /// constant rather than on any observed disagreement/coincidence, and
    /// <see cref="SmoothingSensitivity"/> shows how much they move under a different constant.
    /// </summary>
    bool SmoothingDependent,
    /// <summary>
    /// True when smoothing changes which of m/u is larger — i.e. <c>(rawM &gt; rawU)</c> differs
    /// from <c>(smoothedM &gt; smoothedU)</c> — which is exactly the relation <see cref="Usable"/>
    /// is decided from. A field can hit this WITHOUT <see cref="SmoothingDependent"/>: e.g. a raw m
    /// and raw u both away from 0/1 but computed from very different observation counts, where the
    /// smaller sample gets pulled further toward 0.5 by the same alpha and crosses the larger one.
    /// Whenever true, <see cref="Usable"/> was decided by the smoothing constant, not by the data
    /// alone, and that needs the same scrutiny as SMOOTHING-DEPENDENT even though the raw estimate
    /// itself is not degenerate.
    /// </summary>
    bool SmoothedOrderingDiffersFromRaw,
    /// <summary>
    /// Populated iff <see cref="SmoothingDependent"/> and <see cref="Usable"/>: the same field's
    /// m/u and bits recomputed under two or more smoothing constants (0.5, the primary value used
    /// above, and 1.0, the classic Laplace add-one rule), so the reader can see at a glance how
    /// much of the number is measurement versus modelling choice. Empty otherwise.
    /// </summary>
    IReadOnlyList<SmoothingVariant> SmoothingSensitivity,
    /// <summary>10 buckets over [0,1]: bucket i is [i/10, (i+1)/10), except the last bucket,
    /// which is closed on both ends ([0.9, 1.0]). CONDITIONED same-entity (true-pair) observations
    /// only — sums to <see cref="SameEntityComparisons"/>.</summary>
    IReadOnlyList<long> SameEntitySimilarityHistogram,
    /// <summary>Same bucketing, over EVERY same-entity observation regardless of origin — sums to
    /// <see cref="UnconditionedSameEntityComparisons"/>.</summary>
    IReadOnlyList<long> UnconditionedSameEntitySimilarityHistogram,
    /// <summary>Same bucketing as <see cref="SameEntitySimilarityHistogram"/>, over
    /// different-entity (non-match) observations that were NOT excluded by determination — i.e.
    /// the same population <see cref="DifferentEntityComparisons"/> counts, so the bucket counts
    /// sum to it exactly.</summary>
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
    /// <summary>
    /// Labeled candidate pairs whose owning blocking key could NOT be attributed to exactly one
    /// blocking-role field — either no field's isolated key generation reproduces it, or more than
    /// one field's does (a genuine ambiguity, e.g. a composite key built from several fields' values
    /// together). Such a pair always lands in the unattributable origin bucket, which this service
    /// never excludes from any field's u or m regardless of its measured rate, because guessing
    /// which field to pin the selection reason to would be worse than leaving it in. Reported so
    /// this limitation is visible rather than silently assumed away; see
    /// <see cref="FieldEvidenceCalibrationService"/> class doc.
    /// </summary>
    long UnattributableOwnerCandidatePairs,
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
/// means agreeing on the field is evidence AGAINST a match. This service refuses to emit
/// AgreementBits/DisagreementBits for such a field rather than pick a policy (exclude it, clamp
/// it, floor it) — that decision belongs to whoever applies these numbers, not to this measurement.
/// </para>
/// <para>
/// EMPIRICAL DETERMINATION, SELF ONLY (u AND, identically, m): a candidate pair's owning blocking
/// key — the lowest shared active key <see cref="CorpusAuditService.ForEachCandidatePair"/>
/// already computes to decide which single invocation gets the pair — is attributed to an ORIGIN
/// (a blocking-role field, or <see cref="UnattributableOriginLabel"/>; see
/// <see cref="BuildKeyFieldAttribution"/>). For field F, THIS SERVICE MEASURES — empirically, from
/// the different-entity data, never assumed from provenance alone — the rate at which pairs owned
/// by F's OWN origin agree on F. Only F's own origin can ever be excluded from F's m/u, and only
/// when that measured rate is at or above <see cref="DeterminationThreshold"/>: <em>deriving</em>
/// a key from a field and that key <em>determining</em> agreement on the field are different
/// things. FEBRL's last_name token key IS the surname, so sharing it forces exact agreement
/// (determination ~1.00) — a genuine selection artifact, correctly excluded from both last_name's
/// m and u. SEC's organization_name fingerprint/token/acronym/n-gram keys each capture partial
/// signal — sharing one token of a multi-word company name says almost nothing about whether the
/// FULL names match — so organization_name's own-origin determination stays far below threshold
/// and nothing is excluded: the field's original m and u are exactly what those pairs' true
/// agreement rates are, not an artifact to correct for.
/// <para>
/// A DIFFERENT field's origin is NEVER excluded from F's m/u, no matter how high ITS
/// determination on F measures — only F's own origin is ever eligible. A profile that blocks on
/// postal_code with a separately Matchable state field would see a very high determination rate
/// for postal_code-owned pairs on state (postal codes largely determine state), but state's own m
/// and u are computed from postal_code-owned pairs exactly as before: cross-field correlation is
/// reported (every origin's rate appears in <see cref="FieldCalibrationRow.OriginDeterminations"/>,
/// a genuinely useful diagnostic of how state and postal_code interact under this blocking) but
/// never decides anything for a field it is not. A field that is not itself a blocking-role field
/// at all — or one whose own origin's determination stays below threshold — is therefore
/// STRUCTURALLY unaffected by this whole mechanism: its conditioned and unconditioned m/u are
/// identical, and its numbers are exactly what they would be with no exclusion logic in this
/// service at all.
/// </para>
/// <para>
/// m is conditioned the SAME way u is, using the SAME exclusion decision (computed from the
/// different-entity population, since "determination" is inherently about whether sharing a key
/// forces agreement among candidates that are NOT already known to match): same-entity pairs owned
/// by F's own excluded origin are removed from F's m exactly as the corresponding different-entity
/// pairs are removed from F's u, so <c>log2(conditionedM / u)</c> is a likelihood ratio over ONE
/// population, not m over "every candidate" divided by u over "candidates minus the self-forcing
/// ones". <see cref="FieldCalibrationRow.UnconditionedSameEntityComparisons"/> and its siblings
/// report the unfiltered figure alongside, so the difference conditioning makes is visible.
/// </para>
/// The unattributable origin is never excluded regardless of its rate, since there is no specific
/// field to pin the selection reason to.
/// </para>
/// <para>
/// Key-to-field attribution (<see cref="BuildKeyFieldAttribution"/>) is derived generically, not
/// by inspecting any specific <see cref="IBlockingStrategy"/>'s internals: for each blocking-role
/// field F, every OTHER blocking-role field has its Blocking role stripped from a profile copy,
/// and the resulting "solo" key generation is run over the fit corpus to see which key strings F
/// alone can produce. A key observed in the real (all-fields-active) index is attributed to F only
/// when F's solo set is the ONLY one containing it. A key belonging to zero or more than one
/// field's solo set is left unattributed — that covers both a coincidental cross-field string
/// collision and a genuine multi-field composite key (e.g. <c>CompositeBlockingStrategy</c>, which
/// this technique correctly reports as unattributable: disabling either part field makes the
/// composite strategy return no keys at all, so the composite key appears in neither solo set).
/// </para>
/// </summary>
public sealed class FieldEvidenceCalibrationService
{
    private const int HistogramBuckets = 10;
    private const double AgreementEpsilon = 1e-9;

    /// <summary>The label for candidate pairs whose owning key could not be attributed to exactly
    /// one blocking-role field. This origin is never excluded from any field's m/u, regardless of
    /// its measured determination rate — see the class doc's EMPIRICAL DETERMINATION section.</summary>
    internal const string UnattributableOriginLabel = "(unattributable)";

    /// <summary>
    /// How strongly a field's OWN origin must force agreement on that field, MEASURED from the
    /// different-entity candidate pairs it actually owns, before those pairs (and the matching
    /// same-entity pairs) are excluded from that field's m/u. A judgement call, not a measurement —
    /// 0.95 means "records sharing this field's own blocking key agree on this field at least 95%
    /// of the time among the different-entity candidates it brought together", which is high enough
    /// that counting those pairs would mostly measure the selection rule rather than chance
    /// agreement, without being so close to 1.0 that ordinary sampling noise on a modest
    /// observation count could trip it by accident. Named and commented rather than inlined so a
    /// reader auditing <see cref="OriginDetermination"/> rows — which report the exact rate and
    /// observation count behind every exclusion decision — can see precisely what this number
    /// decided and substitute their own. Applies ONLY to a field's own origin (see class doc); a
    /// cross-field origin's rate is reported but never compared against this constant to decide
    /// anything.
    /// </summary>
    private const double DeterminationThreshold = 0.95;

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

        var normalization = ProfileNormalization.Resolve(_registry, profile);
        var similarity = _registry.Similarity[profile.SimilarityStrategy];

        var normalized = fit.Select(r => normalization.Normalize(r, profile)).ToArray();
        var index = CorpusAuditService.BuildIndex(fit, profile, _registry, ct);
        var effectiveMax = maxBlockSize ?? profile.MaxBlockSize;

        // Same suppressed-key array ForEachCandidatePair computes internally: recomputing a pair's
        // owning key below (to attribute it to a blocking field) must use the identical array, or
        // the two could disagree about which key "won" a pair that shares more than one active key.
        var suppressed = CorpusAuditService.SuppressedKeys(index, effectiveMax);
        var keyFieldAttribution = BuildKeyFieldAttribution(normalized, profile, _registry, index.KeyNames, ct);

        var matchableFields = profile.Fields
            .Where(f => f.Roles.HasFlag(FieldRole.Matchable))
            .Select(f => f.Name)
            .ToList();
        var accumulators = matchableFields.ToDictionary(
            n => n, _ => new FieldAccumulator(HistogramBuckets), StringComparer.Ordinal);

        long emitted = 0, labeledSame = 0, labeledDifferent = 0, unlabeled = 0, unattributableOwner = 0;

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

            // The SAME scan ForEachCandidatePair used internally to decide this pair belongs to
            // its current key — recomputed here (not threaded through, since the shared walk's
            // signature has no room for it) via the internal helper it already exposes, so this is
            // the identical answer, not a second independent judgement.
            var ownerKeyId = CorpusAuditService.LowestSharedActiveKey(
                index.RecordKeys[l], index.RecordKeys[r], suppressed);
            var originLabel = UnattributableOriginLabel;
            if (ownerKeyId >= 0)
            {
                keyFieldAttribution.TryGetValue(index.KeyNames[ownerKeyId], out var ownerField);
                if (ownerField is null) unattributableOwner++;
                else originLabel = ownerField;
            }

            var signals = similarity.Evaluate(normalized[l], normalized[r], profile);
            foreach (var signal in signals)
            {
                // Only Compared observations are agreement/disagreement evidence: Missing/
                // NotComparable signals contribute zero to the evidence scorer regardless of m/u
                // (EvidenceScoringStrategy.Score), so calibrating on them would estimate a
                // probability for an event the scorer never prices.
                if (signal.Outcome != ComparisonOutcome.Compared) continue;
                if (!accumulators.TryGetValue(signal.Name, out var accumulator)) continue;

                // Bucketed by origin on BOTH sides, not yet decided in/out: which origin (if any)
                // gets excluded from THIS field's m/u depends on the AGGREGATE determination rate
                // computed after the whole walk finishes (BuildRow), not on a per-pair rule — see
                // the class doc's EMPIRICAL DETERMINATION section. Same-entity pairs are bucketed
                // by origin too now (not just different-entity), so m can be conditioned exactly
                // the way u is.
                if (sameEntity)
                    accumulator.RecordSame(originLabel, signal.Value);
                else
                    accumulator.RecordDiff(originLabel, signal.Value);
            }
        }, ct);

        var rows = matchableFields.Select(name => BuildRow(name, accumulators[name])).ToList();

        return new FieldEvidenceCalibrationResult(
            records.Count, fit.Count, evalCount, fitFraction,
            occurrences, emitted, labeledSame, labeledDifferent, unlabeled, unattributableOwner, rows);
    }

    /// <summary>
    /// Deterministic fit/eval assignment from a SHA-256 of the record id's UTF-8 bytes, composed
    /// into a ulong byte-by-byte (never via <see cref="BitConverter"/>, whose endianness depends
    /// on the host architecture) so the split is identical across runs AND machines — the
    /// property the task calls out explicitly, and the one plain <c>GetHashCode</c> cannot offer
    /// (randomized per process since .NET Core). Independent of arrival order: the same id
    /// always lands on the same side no matter where it sits in the input file.
    /// <para>
    /// PUBLIC (not merely internal) on purpose: this is the ONE place the fit/eval split is
    /// computed, and any downstream instrument that needs to evaluate honestly on held-out data
    /// — `match corpus audit --eval-only`, `match corpus ablate --eval-only`, `match scoring audit
    /// --eval-only` (see <c>AuditCliCommon.ApplyEvalOnlyFilter</c> in Linkuity.Cli) — must call
    /// this exact function rather than reimplement it. Two independently written hash splits
    /// would diverge silently the first time either one's byte-composition or hash choice drifted,
    /// and nobody downstream would notice until a "held-out" evaluation quietly included fit-half
    /// records again.
    /// </para>
    /// </summary>
    public static bool IsFitHalf(string sourceRecordId, double fitFraction)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sourceRecordId));
        ulong v = 0;
        for (var i = 0; i < 8; i++) v = (v << 8) | hash[i];
        var fraction = v / (double)ulong.MaxValue;
        return fraction < fitFraction;
    }

    /// <summary>
    /// For every key string the real (all-fields-active) index actually contains, which single
    /// blocking-role field — if any, unambiguously — produced it. See the class doc's
    /// "Key-to-field attribution" section for the method and why it correctly reports a genuine
    /// composite (multi-field) key as unattributable rather than guessing. This is provenance
    /// ONLY — it says a key DERIVES from a field, not that the key DETERMINES agreement on that
    /// field; see EMPIRICAL DETERMINATION in the class doc for the (separate, measured) test that
    /// actually decides exclusion.
    /// </summary>
    private static IReadOnlyDictionary<string, string?> BuildKeyFieldAttribution(
        EntityRecord[] normalizedFit, MatchingProfile profile, IStrategyRegistry registry,
        IReadOnlyList<string> observedKeyNames, CancellationToken ct)
    {
        var blockingFieldNames = profile.Fields
            .Where(f => f.Roles.HasFlag(FieldRole.Blocking))
            .Select(f => f.Name)
            .ToList();

        var attribution = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (blockingFieldNames.Count == 0)
        {
            foreach (var key in observedKeyNames) attribution[key] = null;
            return attribution;
        }

        // One pass over the fit corpus per blocking field, with every OTHER blocking field's
        // Blocking role stripped: "what would THIS field alone contribute to the key vocabulary".
        var soloKeySets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var fieldName in blockingFieldNames)
        {
            var soloProfile = profile with
            {
                Fields = profile.Fields.Select(f => f with
                {
                    Roles = string.Equals(f.Name, fieldName, StringComparison.Ordinal)
                        ? f.Roles
                        : f.Roles & ~FieldRole.Blocking
                }).ToList()
            };

            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < normalizedFit.Length; i++)
            {
                if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                foreach (var strategyName in profile.BlockingStrategies)
                    foreach (var key in registry.Blocking[strategyName].GenerateKeys(normalizedFit[i], soloProfile))
                        keys.Add(key);
            }
            soloKeySets[fieldName] = keys;
        }

        foreach (var key in observedKeyNames)
        {
            string? owner = null;
            var matchCount = 0;
            foreach (var (fieldName, set) in soloKeySets)
            {
                if (!set.Contains(key)) continue;
                matchCount++;
                owner = fieldName;
            }
            // Exactly one field's solo generation reproduces this key: unambiguous. Zero (no
            // single field can produce it alone — a genuine composite/joint key) or more than one
            // (a coincidental cross-field string collision) are both left unattributed rather than
            // guessed at.
            attribution[key] = matchCount == 1 ? owner : null;
        }
        return attribution;
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
        // ---- Unconditioned m: every same-entity observation, regardless of origin. ----
        long unconditionedSameCount = 0, unconditionedSameAgree = 0;
        var unconditionedSameHistogram = new long[HistogramBuckets];
        foreach (var bucket in acc.SameByOrigin.Values)
        {
            unconditionedSameCount += bucket.Count;
            unconditionedSameAgree += bucket.Agree;
            for (var i = 0; i < HistogramBuckets; i++) unconditionedSameHistogram[i] += bucket.Histogram[i];
        }
        double? unconditionedRawM = unconditionedSameCount > 0
            ? (double)unconditionedSameAgree / unconditionedSameCount : null;
        var unconditionedSmoothedM = SmoothedRate(unconditionedSameAgree, unconditionedSameCount, PrimarySmoothingAlpha);

        // ---- EMPIRICAL DETERMINATION: measured ONLY on THIS FIELD'S OWN origin (see class doc —
        // this is Critical Fix 1: a cross-field origin's rate is reported but can never exclude
        // anything, regardless of how high it measures). ----
        var determinations = new List<OriginDetermination>();
        var selfExcluded = false;
        foreach (var (originLabel, bucket) in acc.DiffByOrigin
                     .OrderByDescending(kv => kv.Value.Count)
                     .ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var rate = bucket.Count > 0 ? (double)bucket.Agree / bucket.Count : 0.0;
            var isSelf = string.Equals(originLabel, name, StringComparison.Ordinal);
            var excluded = isSelf && rate >= DeterminationThreshold;
            if (excluded) selfExcluded = true;
            determinations.Add(new OriginDetermination(originLabel, bucket.Count, bucket.Agree, rate, excluded));
        }

        // ---- Conditioned m and u: identical population — every origin EXCEPT this field's own,
        // when (and only when) that own-origin's determination crossed the threshold. ----
        long diffCount = 0, diffAgree = 0, excludedDiffCount = 0;
        var diffHistogram = new long[HistogramBuckets];
        foreach (var (originLabel, bucket) in acc.DiffByOrigin)
        {
            if (selfExcluded && string.Equals(originLabel, name, StringComparison.Ordinal))
            {
                excludedDiffCount += bucket.Count;
                continue;
            }
            diffCount += bucket.Count;
            diffAgree += bucket.Agree;
            for (var i = 0; i < HistogramBuckets; i++) diffHistogram[i] += bucket.Histogram[i];
        }

        long sameCount = 0, sameAgree = 0, excludedSameCount = 0;
        var sameHistogram = new long[HistogramBuckets];
        foreach (var (originLabel, bucket) in acc.SameByOrigin)
        {
            if (selfExcluded && string.Equals(originLabel, name, StringComparison.Ordinal))
            {
                excludedSameCount += bucket.Count;
                continue;
            }
            sameCount += bucket.Count;
            sameAgree += bucket.Agree;
            for (var i = 0; i < HistogramBuckets; i++) sameHistogram[i] += bucket.Histogram[i];
        }

        double? rawM = sameCount > 0 ? (double)sameAgree / sameCount : null;
        var smoothedM = SmoothedRate(sameAgree, sameCount, PrimarySmoothingAlpha);
        double? rawU = diffCount > 0 ? (double)diffAgree / diffCount : null;
        var smoothedU = SmoothedRate(diffAgree, diffCount, PrimarySmoothingAlpha);

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

        // Degenerate in ANY direction that would send a bit to +/-infinity without the continuity
        // correction: raw m or raw u at EITHER boundary (0 or 1) — see the SmoothingDependent doc
        // on FieldCalibrationRow. Requires BOTH sides to actually have data: determination-based
        // exclusion can leave a field with diffCount == 0 (every different-entity candidate
        // happened to be excluded — a small fixture can trigger this, and it is a real possibility
        // on a real corpus too), and a field with no u at all has no bits resting on ANY smoothing
        // constant to flag — it belongs in "NO ESTIMATE", not "SMOOTHING-DEPENDENT".
        var smoothingDependent = smoothedM is not null && smoothedU is not null
            && ((rawM is 0.0 or 1.0) || (rawU is 0.0 or 1.0));

        // Independent of smoothingDependent: does smoothing change which of m/u is larger — the
        // EXACT relation Usable is decided from? Possible even with neither raw value at a 0/1
        // boundary, when m and u rest on very different observation counts and the same alpha
        // pulls the thinner side further toward 0.5.
        var orderingDiffers = rawM is { } rmv && rawU is { } ruv && smoothedM is { } smv2 && smoothedU is { } suv2
            && (rmv > ruv) != (smv2 > suv2);

        IReadOnlyList<SmoothingVariant> sensitivity = [];
        if (smoothingDependent && usable)
        {
            sensitivity = new[] { PrimarySmoothingAlpha, SecondarySmoothingAlpha }
                .Select(alpha => BuildVariant(sameAgree, sameCount, diffAgree, diffCount, alpha))
                .ToList();
        }

        return new FieldCalibrationRow(
            name,
            sameCount, sameAgree, rawM, smoothedM, excludedSameCount,
            unconditionedSameCount, unconditionedSameAgree, unconditionedRawM, unconditionedSmoothedM,
            diffCount, diffAgree, rawU, smoothedU, excludedDiffCount,
            determinations,
            agreementBits, disagreementBits, usable, unusableReason,
            smoothingDependent, orderingDiffers, sensitivity,
            sameHistogram, unconditionedSameHistogram, diffHistogram);
    }

    private static SmoothingVariant BuildVariant(
        long sameAgree, long sameCount, long diffAgree, long diffCount, double alpha)
    {
        // Guarded by the smoothingDependent+usable check at the call site, which already implies
        // both sides have at least one observation (sameCount>0 to have a raw m, diffCount>0 to
        // have a raw u), so these are never null here.
        var m = SmoothedRate(sameAgree, sameCount, alpha)!.Value;
        var u = SmoothedRate(diffAgree, diffCount, alpha)!.Value;
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
    /// a record type would make needlessly awkward to update in place. BOTH same-entity and
    /// different-entity observations are kept bucketed BY ORIGIN (see <see cref="OriginBucket"/>)
    /// rather than pre-aggregated, because which origin (if any) is excluded from m/u is not
    /// decided until the whole walk is complete (empirical determination needs the origin's FULL
    /// different-entity observation count) — and the same exclusion decision then applies to both
    /// the same- and different-entity buckets for that origin.</summary>
    private sealed class FieldAccumulator(int buckets)
    {
        private readonly Dictionary<string, OriginBucket> _sameByOrigin = new(StringComparer.Ordinal);
        private readonly Dictionary<string, OriginBucket> _diffByOrigin = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, OriginBucket> SameByOrigin => _sameByOrigin;
        public IReadOnlyDictionary<string, OriginBucket> DiffByOrigin => _diffByOrigin;

        public void RecordSame(string originLabel, double value) => Record(_sameByOrigin, originLabel, value);
        public void RecordDiff(string originLabel, double value) => Record(_diffByOrigin, originLabel, value);

        private void Record(Dictionary<string, OriginBucket> byOrigin, string originLabel, double value)
        {
            if (!byOrigin.TryGetValue(originLabel, out var bucket))
            {
                bucket = new OriginBucket(buckets);
                byOrigin[originLabel] = bucket;
            }
            bucket.Record(value);
        }
    }

    /// <summary>Running tallies for ONE origin's observations (same- or different-entity — two
    /// separate instances per origin) of ONE field.</summary>
    private sealed class OriginBucket(int buckets)
    {
        private readonly long[] _histogram = new long[buckets];

        public long Count { get; private set; }
        public long Agree { get; private set; }
        public IReadOnlyList<long> Histogram => _histogram;

        public void Record(double value)
        {
            var clamped = Math.Clamp(value, 0.0, 1.0);
            var bucket = Math.Min(buckets - 1, (int)(clamped * buckets));
            Count++;
            _histogram[bucket]++;
            if (clamped >= 1.0 - AgreementEpsilon) Agree++;
        }
    }
}
