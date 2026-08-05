using System.Globalization;
using System.Text;
using Linkuity.Pipeline;

namespace Linkuity.Cli;

/// <summary>
/// Human-readable field-evidence calibration report. Every guard rail the measurement carries —
/// zero-observation fields, an UNUSABLE m &lt;= u field, a SMOOTHING-DEPENDENT boundary estimate,
/// a field's own origin excluded from its own m/u by measured determination, smoothing flipping
/// which of m/u is larger — gets its own visible line rather than being folded into the table,
/// because those are exactly the situations someone skimming a wall of numbers is most likely to
/// miss.
/// </summary>
public static class FieldEvidenceCalibrationTextFormatter
{
    public static string Format(FieldEvidenceCalibrationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var sb = new StringBuilder();
        sb.AppendLine("=== field evidence calibration ===");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"records {result.TotalRecords:N0}   fit {result.FitRecords:N0} ({result.FitFraction:P0} target)   " +
            $"eval {result.EvalRecords:N0} (held out, never used below)");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"candidate pairs (fit half) {result.CandidatePairsEmitted:N0} emitted from " +
            $"{result.CandidateOccurrences:N0} block-pair visits");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"labeled same-entity pairs {result.LabeledSameEntityPairs:N0}   " +
            $"labeled different-entity pairs {result.LabeledDifferentEntityPairs:N0}   " +
            $"unlabeled/skipped {result.UnlabeledCandidatePairs:N0}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"candidate pairs whose owning blocking key could not be attributed to one field " +
            $"(never excluded from any field's m/u): {result.UnattributableOwnerCandidatePairs:N0}");
        sb.AppendLine();

        sb.AppendLine("m = P(agree | same entity)   u = P(agree | different entity, candidate pair) — BOTH");
        sb.AppendLine("EXCLUDING pairs owned by THIS FIELD'S OWN origin, only when that origin's MEASURED");
        sb.AppendLine("agreement rate on this field is >= the determination threshold. A DIFFERENT field's");
        sb.AppendLine("origin, however high its rate measures, never excludes anything here — see the per-origin");
        sb.AppendLine("table further down. m/u below are the CONDITIONED figures (same population); the");
        sb.AppendLine("UNCONDITIONED m (every same-entity observation, no origin filtering) is shown alongside");
        sb.AppendLine("whenever it differs.");
        sb.AppendLine("agree := similarity == 1.0 on a Compared signal. m/u below are smoothed");
        sb.AppendLine("((agreements + 0.5) / (comparisons + 1)); raw (unsmoothed) rates are in parentheses.");
        sb.AppendLine("A field marked UNUSABLE emits no bits (see the section below); its m/u are still shown.");
        sb.AppendLine();

        sb.AppendLine("field                     n(same)      m            n(diff)  excluded      u            agree-bits  disagree-bits");
        foreach (var f in result.Fields)
        {
            var agree = f.Usable ? FormatBits(f.AgreementBits) : "UNUSABLE";
            var disagree = f.Usable ? FormatBits(f.DisagreementBits) : "UNUSABLE";
            var flags = (f.Usable ? "" : " [UNUSABLE]")
                + (f.SmoothingDependent ? " [SMOOTHING-DEPENDENT]" : "")
                + (f.SmoothedOrderingDiffersFromRaw ? " [ORDERING-FLIPPED-BY-SMOOTHING]" : "");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"{f.FieldName,-24} {f.SameEntityComparisons,10:N0}  {FormatRate(f.SmoothedM, f.RawM),-12} " +
                $"{f.DifferentEntityComparisons,8:N0} {f.DifferentEntityExcludedByDetermination,9:N0}  {FormatRate(f.SmoothedU, f.RawU),-12} " +
                $"{agree,10}  {disagree,12}{flags}");
        }
        sb.AppendLine();

        var conditioned = result.Fields.Where(f => f.SameEntityExcludedByDetermination > 0).ToList();
        if (conditioned.Count > 0)
        {
            sb.AppendLine("CONDITIONED VS UNCONDITIONED m — these fields' own origin was excluded from u, so the");
            sb.AppendLine("SAME same-entity pairs are excluded from m too (m and u must come from one population,");
            sb.AppendLine("not m over every candidate divided by u over a subset). Unconditioned m — every same-");
            sb.AppendLine("entity observation, no filtering — is shown for comparison; it is NOT what bits use:");
            foreach (var f in conditioned)
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  {f.FieldName}: conditioned m={FormatRate(f.SmoothedM, f.RawM)}  " +
                    $"(n={f.SameEntityComparisons:N0}, excluded={f.SameEntityExcludedByDetermination:N0})   " +
                    $"unconditioned m={FormatRate(f.UnconditionedSmoothedM, f.UnconditionedRawM)}  " +
                    $"(n={f.UnconditionedSameEntityComparisons:N0})");
            sb.AppendLine();
        }

        var withOrigins = result.Fields.Where(f => f.OriginDeterminations.Count > 0).ToList();
        if (withOrigins.Count > 0)
        {
            sb.AppendLine("PER-ORIGIN DETERMINATION — for every origin (a blocking-role field, or the unattributable");
            sb.AppendLine("bucket) that owned at least one different-entity candidate for this field, the rate at");
            sb.AppendLine("which those candidates agree on THIS field. ONLY the row whose origin equals this field's");
            sb.AppendLine("own name can ever be EXCLUDED (and only when its rate is >= 0.95); every other row is a");
            sb.AppendLine("cross-field diagnostic that never decides anything, however high it measures. n is the");
            sb.AppendLine("observation count behind the rate — a rate from a handful of pairs is not the same kind");
            sb.AppendLine("of number as one from thousands:");
            foreach (var f in withOrigins)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {f.FieldName}:");
                foreach (var d in f.OriginDeterminations)
                {
                    var self = string.Equals(d.OriginLabel, f.FieldName, StringComparison.Ordinal) ? " (own)" : "";
                    sb.AppendLine(CultureInfo.InvariantCulture,
                        $"    origin={d.OriginLabel,-20}{self,-6} n={d.Observations,10:N0}  agree={d.Agreements,10:N0}  " +
                        $"rate={d.DeterminationRate,8:P2}  {(d.Excluded ? "EXCLUDED" : "kept")}");
                }
            }
            sb.AppendLine();
        }

        var noObservations = result.Fields.Where(f => f.SmoothedM is null || f.SmoothedU is null).ToList();
        if (noObservations.Count > 0)
        {
            sb.AppendLine("NO ESTIMATE — zero labeled comparisons in one or both classes (bits cannot be computed):");
            foreach (var f in noObservations)
            {
                var excludedNote = f.DifferentEntityExcludedByDetermination > 0
                    ? $"  (excluded by determination: {f.DifferentEntityExcludedByDetermination:N0})"
                    : "";
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  {f.FieldName}: n(same)={f.SameEntityComparisons:N0}  n(diff)={f.DifferentEntityComparisons:N0}{excludedNote}");
            }
            sb.AppendLine();
        }

        var unusable = result.Fields.Where(f => !f.Usable).ToList();
        if (unusable.Count > 0)
        {
            sb.AppendLine("*** UNUSABLE: m <= u, so evidence from these fields DECREASES AS SIMILARITY INCREASES. ***");
            sb.AppendLine("No AgreementBits/DisagreementBits are emitted for them. Almost always a misconfigured");
            sb.AppendLine("field or evaluator. Deciding what to do about it is a separate judgement:");
            foreach (var f in unusable)
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  {f.FieldName}: m={f.SmoothedM:F6}  u={f.SmoothedU:F6}  -- {f.UnusableReason}");
            sb.AppendLine();
        }

        var smoothingDependent = result.Fields.Where(f => f.SmoothingDependent && f.Usable).ToList();
        if (smoothingDependent.Count > 0)
        {
            sb.AppendLine("SMOOTHING-DEPENDENT — raw m or raw u sits at 0 or 1, so the bits below rest entirely on");
            sb.AppendLine("the continuity-correction constant rather than on any observed disagreement/coincidence.");
            sb.AppendLine("Shown under two constants so the sensitivity is visible, not just the primary number:");
            foreach (var f in smoothingDependent)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {f.FieldName}:");
                foreach (var v in f.SmoothingSensitivity)
                    sb.AppendLine(CultureInfo.InvariantCulture,
                        $"    alpha={v.Alpha,4:F1}   m={v.SmoothedM:F6}  u={v.SmoothedU:F6}   " +
                        $"agree-bits={FormatBits(v.AgreementBits),8}  disagree-bits={FormatBits(v.DisagreementBits),8}");
            }
            sb.AppendLine();
        }

        var orderingFlipped = result.Fields.Where(f => f.SmoothedOrderingDiffersFromRaw).ToList();
        if (orderingFlipped.Count > 0)
        {
            sb.AppendLine("*** ORDERING-FLIPPED-BY-SMOOTHING: raw and smoothed m/u disagree on which is larger. ***");
            sb.AppendLine("Usable is decided from the SMOOTHED values, so for these fields the smoothing constant —");
            sb.AppendLine("not the data alone — decided whether this field is usable:");
            foreach (var f in orderingFlipped)
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  {f.FieldName}: raw m={FormatRawOnly(f.RawM)} raw u={FormatRawOnly(f.RawU)}   " +
                    $"smoothed m={f.SmoothedM:F6} smoothed u={f.SmoothedU:F6}");
            sb.AppendLine();
        }

        sb.AppendLine("similarity distribution (10 buckets over [0,1], last bucket closed at 1.0):");
        foreach (var f in result.Fields)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {f.FieldName} same-entity (conditioned): {Histogram(f.SameEntitySimilarityHistogram)}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {f.FieldName} different-entity:          {Histogram(f.DifferentEntitySimilarityHistogram)}");
        }

        return sb.ToString();
    }

    private static string FormatRate(double? smoothed, double? raw)
        => smoothed is { } s
            ? $"{s.ToString("F6", CultureInfo.InvariantCulture)}({FormatRawOnly(raw)})"
            : "n/a";

    private static string FormatRawOnly(double? raw)
        => raw is { } r ? r.ToString("F4", CultureInfo.InvariantCulture) : "n/a";

    private static string FormatBits(double? bits)
        => bits is { } b ? b.ToString("F3", CultureInfo.InvariantCulture) : "n/a";

    private static string Histogram(IReadOnlyList<long> buckets)
        => string.Join(" ", buckets.Select(b => b.ToString("N0", CultureInfo.InvariantCulture)));
}
