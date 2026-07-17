using System.Globalization;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Numeric similarity. 1.0 when the parsed values are equal. With
/// EvaluatorOptions["numeric.tolerance"] (absolute) similarity decays linearly to
/// 0 at the tolerance. Else with EvaluatorOptions["numeric.maxPercentDiff"]
/// (fractional, e.g. "0.1" = 10%) it decays linearly to 0 at that percentage.
/// Else it falls back to 1 - relativeDifference. Returns null when either side is
/// unparseable (non-comparable).
/// </summary>
public sealed class NumericSimilarityEvaluator : ISimilarityEvaluator
{
    public string Name => "numeric";

    public double? Evaluate(string left, string right, ProfileField field)
    {
        if (!TryParse(left, out var a) || !TryParse(right, out var b))
            return null;
        if (a == b)
            return 1.0;

        var tolerance = ReadOption(field, "numeric.tolerance");
        if (tolerance is > 0)
        {
            var diff = Math.Abs(a - b);
            return diff >= tolerance.Value ? 0.0 : 1.0 - diff / tolerance.Value;
        }

        var scale = Math.Max(Math.Abs(a), Math.Abs(b));
        if (scale == 0)
            return 1.0;
        var percentDiff = Math.Abs(a - b) / scale;

        var maxPercentDiff = ReadOption(field, "numeric.maxPercentDiff");
        if (maxPercentDiff is > 0)
            return percentDiff >= maxPercentDiff.Value ? 0.0 : 1.0 - percentDiff / maxPercentDiff.Value;

        return Math.Max(0.0, 1.0 - percentDiff);
    }

    private static bool TryParse(string value, out double parsed)
        => double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed);

    private static double? ReadOption(ProfileField field, string key)
        => field.EvaluatorOptions is not null
           && field.EvaluatorOptions.TryGetValue(key, out var raw)
           && double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
