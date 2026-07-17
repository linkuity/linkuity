using System.Globalization;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Date proximity similarity. 1.0 when the parsed dates fall on the same day, then
/// linear decay to 0 across EvaluatorOptions["date.maxDays"] (default 365). Returns
/// null when either side is unparseable (non-comparable).
/// </summary>
public sealed class DateSimilarityEvaluator : ISimilarityEvaluator
{
    private const double DefaultMaxDays = 365.0;

    public string Name => "date";

    public double? Evaluate(string left, string right, ProfileField field)
    {
        if (!TryParse(left, out var a) || !TryParse(right, out var b))
            return null;

        var maxDays = ResolveMaxDays(field);
        var days = Math.Abs((a.Date - b.Date).TotalDays);
        if (days == 0)
            return 1.0;
        if (days >= maxDays)
            return 0.0;
        return 1.0 - days / maxDays;
    }

    private static bool TryParse(string value, out DateTime parsed)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed);

    private static double ResolveMaxDays(ProfileField field)
        => field.EvaluatorOptions is not null
           && field.EvaluatorOptions.TryGetValue("date.maxDays", out var raw)
           && double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
           && value > 0
            ? value
            : DefaultMaxDays;
}
