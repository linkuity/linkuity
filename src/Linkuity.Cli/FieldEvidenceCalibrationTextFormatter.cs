using System.Globalization;
using System.Text;
using Linkuity.Pipeline;

namespace Linkuity.Cli;

/// <summary>
/// Human-readable field-evidence calibration report. Every guard rail the measurement carries —
/// zero-observation fields, an UNUSABLE m &lt;= u field, a SMOOTHING-DEPENDENT boundary estimate,
/// self-blocked pairs excluded from a field's own u — gets its own visible line rather than being
/// folded into the table, because those are exactly the situations someone skimming a wall of
/// numbers is most likely to miss.
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
            $"(excluded from no field's u): {result.UnattributableOwnerCandidatePairs:N0}");
        sb.AppendLine();

        sb.AppendLine("m = P(agree | same entity)   u = P(agree | different entity, candidate pair,");
        sb.AppendLine("EXCLUDING pairs THIS field's own blocking key brought together — see 'excluded' below).");
        sb.AppendLine("agree := similarity == 1.0 on a Compared signal. m/u below are smoothed");
        sb.AppendLine("((agreements + 0.5) / (comparisons + 1)); raw (unsmoothed) rates are in parentheses.");
        sb.AppendLine("A field marked UNUSABLE emits no bits (see the section below); its m/u are still shown.");
        sb.AppendLine();

        sb.AppendLine("field                     n(same)      m            n(diff)  excluded      u            agree-bits  disagree-bits");
        foreach (var f in result.Fields)
        {
            var agree = f.Usable ? FormatBits(f.AgreementBits) : "UNUSABLE";
            var disagree = f.Usable ? FormatBits(f.DisagreementBits) : "UNUSABLE";
            var flags = (f.Usable ? "" : " [UNUSABLE]") + (f.SmoothingDependent ? " [SMOOTHING-DEPENDENT]" : "");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"{f.FieldName,-24} {f.SameEntityComparisons,10:N0}  {FormatRate(f.SmoothedM, f.RawM),-12} " +
                $"{f.DifferentEntityComparisons,8:N0} {f.DifferentEntitySelfBlockedExcluded,9:N0}  {FormatRate(f.SmoothedU, f.RawU),-12} " +
                $"{agree,10}  {disagree,12}{flags}");
        }
        sb.AppendLine();

        var selfBlocked = result.Fields.Where(f => f.DifferentEntitySelfBlockedExcluded > 0).ToList();
        if (selfBlocked.Count > 0)
        {
            sb.AppendLine("SELF-BLOCKED EXCLUSION — these fields are themselves blocking keys, so candidate pairs");
            sb.AppendLine("they alone brought together were excluded from their OWN u (they agree by construction,");
            sb.AppendLine("which is not chance agreement). A field with a small remainder after exclusion is exactly");
            sb.AppendLine("where the u estimate gets shaky — check its n(diff) above, not just that a number exists:");
            foreach (var f in selfBlocked)
            {
                var remaining = f.DifferentEntityComparisons;
                var total = remaining + f.DifferentEntitySelfBlockedExcluded;
                var pct = total == 0 ? 0.0 : (double)f.DifferentEntitySelfBlockedExcluded / total;
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  {f.FieldName}: excluded {f.DifferentEntitySelfBlockedExcluded:N0} of {total:N0} " +
                    $"different-entity candidates ({pct:P1}); {remaining:N0} remain for u.");
            }
            sb.AppendLine();
        }

        var noObservations = result.Fields.Where(f => f.SmoothedM is null || f.SmoothedU is null).ToList();
        if (noObservations.Count > 0)
        {
            sb.AppendLine("NO ESTIMATE — zero labeled comparisons in one or both classes (bits cannot be computed):");
            foreach (var f in noObservations)
            {
                var excludedNote = f.DifferentEntitySelfBlockedExcluded > 0
                    ? $"  (excluded as self-blocked: {f.DifferentEntitySelfBlockedExcluded:N0})"
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
            sb.AppendLine("SMOOTHING-DEPENDENT — raw m == 1 or raw u == 0, so the bits below rest entirely on the");
            sb.AppendLine("continuity-correction constant rather than on any observed disagreement/coincidence.");
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

        sb.AppendLine("similarity distribution (10 buckets over [0,1], last bucket closed at 1.0):");
        foreach (var f in result.Fields)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {f.FieldName} same-entity:      {Histogram(f.SameEntitySimilarityHistogram)}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {f.FieldName} different-entity: {Histogram(f.DifferentEntitySimilarityHistogram)}");
        }

        return sb.ToString();
    }

    private static string FormatRate(double? smoothed, double? raw)
        => smoothed is { } s
            ? $"{s.ToString("F6", CultureInfo.InvariantCulture)}({FormatRaw(raw)})"
            : "n/a";

    private static string FormatRaw(double? raw)
        => raw is { } r ? r.ToString("F4", CultureInfo.InvariantCulture) : "n/a";

    private static string FormatBits(double? bits)
        => bits is { } b ? b.ToString("F3", CultureInfo.InvariantCulture) : "n/a";

    private static string Histogram(IReadOnlyList<long> buckets)
        => string.Join(" ", buckets.Select(b => b.ToString("N0", CultureInfo.InvariantCulture)));
}
