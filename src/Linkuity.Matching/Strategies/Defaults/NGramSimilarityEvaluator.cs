using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Character n-gram Dice coefficient (2·|A∩B| / (|A|+|B|)) over normalized values.
/// N-gram size comes from EvaluatorOptions["ngram.size"] (default 3). Short values
/// (shorter than the n-gram size) fall back to the whole normalized string as a
/// single gram. Returns null when either side normalizes to empty.
/// </summary>
public sealed class NGramSimilarityEvaluator : ISimilarityEvaluator
{
    private const int DefaultSize = 3;

    public string Name => "ngram";

    public double? Evaluate(string left, string right, ProfileField field)
    {
        var size = ResolveSize(field);
        var leftGrams = NGrams(MatchKey.Normalize(left), size);
        var rightGrams = NGrams(MatchKey.Normalize(right), size);
        if (leftGrams.Count == 0 || rightGrams.Count == 0)
            return null;

        var intersection = leftGrams.Intersect(rightGrams, StringComparer.Ordinal).Count();
        return 2.0 * intersection / (leftGrams.Count + rightGrams.Count);
    }

    private static int ResolveSize(ProfileField field)
    {
        if (field.EvaluatorOptions is not null
            && field.EvaluatorOptions.TryGetValue("ngram.size", out var raw)
            && int.TryParse(raw, out var parsed)
            && parsed > 0)
            return parsed;
        return DefaultSize;
    }

    private static HashSet<string> NGrams(string value, int size)
    {
        var grams = new HashSet<string>(StringComparer.Ordinal);
        if (value.Length == 0)
            return grams;
        if (value.Length < size)
        {
            grams.Add(value);
            return grams;
        }
        for (var i = 0; i + size <= value.Length; i++)
            grams.Add(value.Substring(i, size));
        return grams;
    }
}
