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
/// One origin's contribution to a field's u estimate, and whether it was excluded — see
/// "EMPIRICAL DETERMINATION" in the <see cref="FieldEvidenceCalibrationService"/> class doc.
/// <see cref="OriginLabel"/> is the blocking-role field the owning key was attributed to, or
/// <see cref="FieldEvidenceCalibrationService.UnattributableOriginLabel"/> when it could not be
/// (see <see cref="FieldEvidenceCalibrationService.BuildKeyFieldAttribution"/>) — that origin is
/// NEVER excluded, regardless of its rate, because there is no known field to pin the selection
/// reason to. <see cref="Observations"/> is reported alongside the rate for exactly the reason the
/// task called out: a rate computed from a handful of pairs is not the same kind of number as one
/// computed from thousands, and a reader must be able to tell them apart at a glance.
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
    /// <summary>Different-entity Compared observations actually used to estimate u — i.e. AFTER
    /// excluding pairs owned by an origin whose empirically measured determination rate on THIS
    /// field is at or above the threshold (see <see cref="DifferentEntityExcludedByDetermination"/>
    /// and <see cref="OriginDeterminations"/>).</summary>
    long DifferentEntityComparisons,
    long DifferentEntityAgreements,
    double? RawU,
    double? SmoothedU,
    /// <summary>
    /// Different-entity candidate pairs excluded from <see cref="DifferentEntityComparisons"/> /
    /// ChanceAgreement because they were owned by an origin whose measured agreement rate on this
    /// field was at or above the determination threshold — see <see cref="OriginDeterminations"/>
    /// for the per-origin breakdown that produced this total, and the class doc's "EMPIRICAL
    /// DETERMINATION" section for why this is measured per origin rather than assumed from
    /// provenance alone. Zero for a field with no self-forcing origin.
    /// </summary>
    long DifferentEntityExcludedByDetermination,
    /// <summary>
    /// Every origin (blocking-role field, or the unattributable bucket) that owned at least one of
    /// this field's different-entity candidate observations, with its own observation count,
    /// agreement count, empirical determination rate, and whether it was excluded. This is the
    /// evidence behind <see cref="DifferentEntityExcludedByDetermination"/> — a reader can see
    /// exactly which origin(s) crossed the threshold, on how much data, rather than a bare count.
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
    /// never excludes from any field's u regardless of its measured rate, because guessing which
    /// field to pin the selection reason to would be worse than leaving it in. Reported so this
    /// limitation is visible rather than silently assumed away; see
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
/// EMPIRICAL DETERMINATION (u only): a candidate pair's owning blocking key — the lowest shared
/// active key <see cref="CorpusAuditService.ForEachCandidatePair"/> already computes to decide
/// which single invocation gets the pair — is attributed to an ORIGIN (a blocking-role field, or
/// <see cref="UnattributableOriginLabel"/>; see <see cref="BuildKeyFieldAttribution"/>). For each
/// field F and each origin that owns at least one of F's different-entity candidate observations,
/// this service measures — empirically, from the data, not from provenance alone — the rate at
/// which those pairs agree on F. An origin's pairs are excluded from F's u estimate ONLY when that
/// measured rate is at or above <see cref="DeterminationThreshold"/>: <em>deriving</em> a key from
/// a field and that key <em>determining</em> agreement on the field are different things. FEBRL's
/// last_name token key IS the surname, so sharing it forces exact agreement (determination ~0.99)
/// — a genuine selection artifact, correctly excluded. SEC's organization_name fingerprint/token/
/// acronym/n-gram keys each capture partial signal — sharing one token of a multi-word company
/// name says almost nothing about whether the FULL names match — so their determination on
/// organization_name stays far below threshold and nothing is excluded: the field's original u is
/// exactly what those pairs' true chance-agreement rate is, not an artifact to correct for. A prior
/// version of this service excluded whenever a key merely DERIVED from a field, regardless of
/// whether it actually forced agreement; that discarded SEC's entire different-entity population to
/// fix a problem specific to FEBRL's last_name, which this measurement — not this field — was
/// missing. The unattributable origin is never excluded regardless of its rate, since there is no
/// specific field to pin the selection reason to. m (same-entity agreement) is NOT filtered this
/// way at all — only u.
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
    /// one blocking-role field. This origin is never excluded from any field's u, regardless of
    /// its measured determination rate — see the class doc's EMPIRICAL DETERMINATION section.</summary>
    internal const string UnattributableOriginLabel = "(unattributable)";

    /// <summary>
    /// How strongly an origin must force agreement on a field, MEASURED from the different-entity
    /// candidate pairs it actually owns, before those pairs are excluded from that field's OWN u.
    /// A judgement call, not a measurement — 0.95 means "records sharing this origin's key agree on
    /// this field at least 95% of the time among the different-entity candidates it brought
    /// together", which is high enough that counting those pairs toward u would mostly measure the
    /// selection rule rather than chance agreement, without being so close to 1.0 that ordinary
    /// sampling noise on a modest observation count could trip it by accident. Named and commented
    /// rather than inlined so a reader auditing <see cref="OriginDetermination"/> rows — which
    /// report the exact rate and observation count behind every exclusion decision — can see
    /// precisely what this number decided and substitute their own.
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

        var normalization = _registry.Normalization[profile.NormalizationStrategy];
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

                if (sameEntity)
                    accumulator.RecordSame(signal.Value);
                else
                    // Bucketed by origin, not yet decided in/out: which origins get excluded from
                    // THIS field's u depends on the AGGREGATE determination rate computed after the
                    // whole walk finishes (BuildRow), not on a per-pair rule — see the class doc's
                    // EMPIRICAL DETERMINATION section.
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
    /// </summary>
    internal static bool IsFitHalf(string sourceRecordId, double fitFraction)
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
        double? rawM = acc.SameCount > 0 ? (double)acc.SameAgree / acc.SameCount : null;
        var smoothedM = SmoothedRate(acc.SameAgree, acc.SameCount, PrimarySmoothingAlpha);

        // EMPIRICAL DETERMINATION: decide, per origin, whether it forces agreement on THIS field
        // ONLY NOW — after the whole walk, once every origin's full observation count is known —
        // never per pair during the walk itself. See the class doc.
        var determinations = new List<OriginDetermination>();
        long diffCount = 0, diffAgree = 0, excludedCount = 0;
        var diffHistogram = new long[HistogramBuckets];

        foreach (var (originLabel, bucket) in acc.DiffByOrigin
                     .OrderByDescending(kv => kv.Value.Count)
                     .ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var rate = bucket.Count > 0 ? (double)bucket.Agree / bucket.Count : 0.0;
            // The unattributable origin is NEVER excluded, no matter how high its rate measures:
            // there is no specific field to pin the selection reason to, so refusing to exclude is
            // the conservative default — see the class doc's EMPIRICAL DETERMINATION section.
            var excluded = !string.Equals(originLabel, UnattributableOriginLabel, StringComparison.Ordinal)
                           && rate >= DeterminationThreshold;
            determinations.Add(new OriginDetermination(originLabel, bucket.Count, bucket.Agree, rate, excluded));

            if (excluded)
            {
                excludedCount += bucket.Count;
            }
            else
            {
                diffCount += bucket.Count;
                diffAgree += bucket.Agree;
                for (var i = 0; i < HistogramBuckets; i++) diffHistogram[i] += bucket.Histogram[i];
            }
        }

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

        // Degenerate in the direction that would send a bit to +/-infinity without the
        // continuity correction — see the SmoothingDependent doc on FieldCalibrationRow. Requires
        // BOTH sides to actually have data: determination-based exclusion can leave a field with
        // diffCount == 0 (every different-entity candidate happened to be excluded — a small
        // fixture can trigger this, and it is a real possibility on a real corpus too), and a
        // field with no u at all has no bits resting on ANY smoothing constant to flag — it
        // belongs in "NO ESTIMATE", not "SMOOTHING-DEPENDENT".
        var smoothingDependent = smoothedM is not null && smoothedU is not null && (rawM is 1.0 || rawU is 0.0);

        IReadOnlyList<SmoothingVariant> sensitivity = [];
        if (smoothingDependent && usable)
        {
            sensitivity = new[] { PrimarySmoothingAlpha, SecondarySmoothingAlpha }
                .Select(alpha => BuildVariant(acc.SameAgree, acc.SameCount, diffAgree, diffCount, alpha))
                .ToList();
        }

        return new FieldCalibrationRow(
            name,
            acc.SameCount, acc.SameAgree, rawM, smoothedM,
            diffCount, diffAgree, rawU, smoothedU,
            excludedCount, determinations,
            agreementBits, disagreementBits, usable, unusableReason,
            smoothingDependent, sensitivity,
            acc.SameHistogram, diffHistogram);
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
    /// a record type would make needlessly awkward to update in place. Different-entity
    /// observations are kept bucketed BY ORIGIN (see <see cref="OriginBucket"/>) rather than
    /// pre-aggregated, because which origins are excluded from u is not decided until the whole
    /// walk is complete (empirical determination needs each origin's FULL observation count).</summary>
    private sealed class FieldAccumulator(int buckets)
    {
        private readonly long[] _sameHistogram = new long[buckets];
        private readonly Dictionary<string, OriginBucket> _diffByOrigin = new(StringComparer.Ordinal);

        public long SameCount { get; private set; }
        public long SameAgree { get; private set; }
        public IReadOnlyList<long> SameHistogram => _sameHistogram;
        public IReadOnlyDictionary<string, OriginBucket> DiffByOrigin => _diffByOrigin;

        /// <summary>Same-entity observations are never filtered by origin (see class doc) —
        /// only different-entity observations are, and only after the whole walk completes.</summary>
        public void RecordSame(double value)
        {
            var (bucket, agree) = Bucket(value, buckets);
            SameCount++;
            _sameHistogram[bucket]++;
            if (agree) SameAgree++;
        }

        public void RecordDiff(string originLabel, double value)
        {
            if (!_diffByOrigin.TryGetValue(originLabel, out var bucket))
            {
                bucket = new OriginBucket(buckets);
                _diffByOrigin[originLabel] = bucket;
            }
            bucket.Record(value);
        }

        private static (int Bucket, bool Agree) Bucket(double value, int bucketCount)
        {
            var clamped = Math.Clamp(value, 0.0, 1.0);
            var bucket = Math.Min(bucketCount - 1, (int)(clamped * bucketCount));
            return (bucket, clamped >= 1.0 - AgreementEpsilon);
        }
    }

    /// <summary>Running tallies for ONE origin's different-entity observations of ONE field.</summary>
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
