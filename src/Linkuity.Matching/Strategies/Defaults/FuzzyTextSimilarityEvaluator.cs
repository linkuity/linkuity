using FuzzySharp;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Fuzzy text similarity using FuzzySharp. Combines edit-distance ratio (catches
/// typos), partial ratio (catches substring overlap), and token-set ratio
/// (catches transposed/reordered tokens) by taking the strongest of the three,
/// scaled to [0,1]. Returns null when either side is blank (non-comparable).
/// </summary>
public sealed class FuzzyTextSimilarityEvaluator : ISimilarityEvaluator
{
    public string Name => "fuzzy";

    public double? Evaluate(string left, string right, ProfileField field)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return null;

        var ratio = Fuzz.Ratio(left, right);
        var partial = Fuzz.PartialRatio(left, right);
        var tokenSet = Fuzz.TokenSetRatio(left, right);
        var best = Math.Max(ratio, Math.Max(partial, tokenSet));
        return best / 100.0;
    }
}
