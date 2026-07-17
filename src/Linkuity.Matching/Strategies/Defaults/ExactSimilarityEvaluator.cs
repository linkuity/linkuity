using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Exact similarity over the durable match-key normalization: 1.0 when the two
/// values normalize to the same non-empty token, 0.0 when they differ, null when
/// either normalizes to empty (non-comparable). Works for strings, identifiers,
/// and digit-only numerics since Normalize keeps only letters and digits.
/// </summary>
public sealed class ExactSimilarityEvaluator : ISimilarityEvaluator
{
    public string Name => "exact";

    public double? Evaluate(string left, string right, ProfileField field)
    {
        var normalizedLeft = MatchKey.Normalize(left);
        var normalizedRight = MatchKey.Normalize(right);
        if (normalizedLeft.Length == 0 || normalizedRight.Length == 0)
            return null;
        return string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal) ? 1.0 : 0.0;
    }
}
