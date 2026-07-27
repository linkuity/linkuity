using Linkuity.Matching.Canonicalization;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Jaccard similarity over canonicalized token sets. When the field's semantic type
/// has a canonicalizer registered in <see cref="TokenCanonicalizers.Default"/>
/// (today: OrganizationName), both sides are canonicalized first — leading articles
/// dropped, trailing legal suffixes stripped, ampersand initials collapsed — so
/// "THE BOEING COMPANY" and "BOEING CO" score 1.0 while "THE WALT DISNEY COMPANY"
/// vs "THE BOEING COMPANY" scores 0.0 (shared THE/COMPANY no longer count). Fields
/// without a registered canonicalizer get the raw-token semantics of
/// <see cref="JaccardSimilarityEvaluator"/>. Returns null when either side yields
/// no tokens (non-comparable).
/// </summary>
public sealed class CanonicalJaccardSimilarityEvaluator : ISimilarityEvaluator
{
    private static readonly JaccardSimilarityEvaluator RawFallback = new();

    public string Name => "canonical-jaccard";

    public double? Evaluate(string left, string right, ProfileField field)
    {
        if (!TokenCanonicalizers.Default.TryGetValue(field.SemanticType, out var canonicalizer))
            return RawFallback.Evaluate(left, right, field);

        var leftTokens = canonicalizer.Canonicalize(left).ToHashSet(StringComparer.Ordinal);
        var rightTokens = canonicalizer.Canonicalize(right).ToHashSet(StringComparer.Ordinal);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
            return null;

        var intersection = leftTokens.Intersect(rightTokens, StringComparer.Ordinal).Count();
        var union = leftTokens.Union(rightTokens, StringComparer.Ordinal).Count();
        return (double)intersection / union;
    }
}
