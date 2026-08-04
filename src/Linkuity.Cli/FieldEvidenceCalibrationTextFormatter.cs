using System.Globalization;
using System.Text;
using Linkuity.Pipeline;

namespace Linkuity.Cli;

/// <summary>
/// Human-readable field-evidence calibration report. Every guard rail the measurement carries —
/// zero-observation fields, m/u at the raw 0/1 boundary, m &lt;= u — gets its own visible line
/// rather than being folded into the table, because those are exactly the situations someone
/// skimming a wall of numbers is most likely to miss.
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
        sb.AppendLine();

        sb.AppendLine("m = P(agree | same entity)   u = P(agree | different entity, candidate pair)");
        sb.AppendLine("agree := similarity == 1.0 on a Compared signal. m/u below are Laplace-smoothed");
        sb.AppendLine("((agreements + 0.5) / (comparisons + 1)); raw (unsmoothed) rates are in parentheses.");
        sb.AppendLine();

        sb.AppendLine("field                     n(same)      m            n(diff)      u            agree-bits  disagree-bits");
        foreach (var f in result.Fields)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"{f.FieldName,-24} {f.SameEntityComparisons,10:N0}  {FormatRate(f.SmoothedM, f.RawM),-12} " +
                $"{f.DifferentEntityComparisons,10:N0}  {FormatRate(f.SmoothedU, f.RawU),-12} " +
                $"{FormatBits(f.AgreementBits),10}  {FormatBits(f.DisagreementBits),12}");
        }
        sb.AppendLine();

        var noObservations = result.Fields.Where(f => f.SmoothedM is null || f.SmoothedU is null).ToList();
        if (noObservations.Count > 0)
        {
            sb.AppendLine("NO ESTIMATE — zero labeled comparisons in one or both classes (bits cannot be computed):");
            foreach (var f in noObservations)
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  {f.FieldName}: n(same)={f.SameEntityComparisons:N0}  n(diff)={f.DifferentEntityComparisons:N0}");
            sb.AppendLine();
        }

        var boundary = result.Fields.Where(f =>
            (f.RawM is 0.0 or 1.0) || (f.RawU is 0.0 or 1.0)).ToList();
        if (boundary.Count > 0)
        {
            sb.AppendLine("RAW ESTIMATE AT THE 0/1 BOUNDARY — FieldEvidence refuses these outright; the m/u " +
                          "above are the Laplace-smoothed stand-in, not a measurement free of assumptions:");
            foreach (var f in boundary)
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  {f.FieldName}: raw m={FormatRaw(f.RawM)} (n={f.SameEntityComparisons:N0})   " +
                    $"raw u={FormatRaw(f.RawU)} (n={f.DifferentEntityComparisons:N0})");
            sb.AppendLine();
        }

        var inverted = result.Fields.Where(f => f.EvidenceInverted).ToList();
        if (inverted.Count > 0)
        {
            sb.AppendLine("*** m <= u: AGREEING ON THIS FIELD IS EVIDENCE AGAINST A MATCH. ***");
            sb.AppendLine("This is almost always a misconfigured field or evaluator, not a real finding:");
            foreach (var f in inverted)
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  {f.FieldName}: m={f.SmoothedM:F6}  u={f.SmoothedU:F6}");
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
