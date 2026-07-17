using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Token Jaccard similarity (|A∩B| / |A∪B|) over the durable tokenization, so
/// "Jane Doe" and "doe jane" score 1.0. Returns null when either side tokenizes
/// to nothing (non-comparable).
/// </summary>
public sealed class JaccardSimilarityEvaluator : ISimilarityEvaluator
{
    public string Name => "jaccard";

    public double? Evaluate(string left, string right, ProfileField field)
    {
        var leftTokens = MatchKey.Tokens(left).ToHashSet(StringComparer.Ordinal);
        var rightTokens = MatchKey.Tokens(right).ToHashSet(StringComparer.Ordinal);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
            return null;

        var intersection = leftTokens.Intersect(rightTokens, StringComparer.Ordinal).Count();
        var union = leftTokens.Union(rightTokens, StringComparer.Ordinal).Count();
        return (double)intersection / union;
    }
}
