namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// The durable matcher's internal match-key normalization, ported verbatim from
/// FileMetadataStore (Normalize/Tokens/TokenSimilarity/SharedExact/IsNonCanonicalField).
/// This is deliberately distinct from FieldNormalizer: it is the lightweight
/// key/token normalization the current Score and blocking-key logic use, and it
/// must reproduce that behavior exactly.
/// </summary>
internal static class MatchKey
{
    private static readonly char[] TokenDelimiters =
        [' ', '\t', '\r', '\n', '.', ',', ';', ':', '-', '_', '@', '/', '\\', '|', '&', '(', ')'];

    public static string Normalize(string value)
        => string.Join("", value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit));

    public static IEnumerable<string> Tokens(string value)
        => value.ToLowerInvariant()
            .Split(TokenDelimiters, StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .Where(token => token.Length > 0);

    public static bool IsNonCanonicalField(string field)
        => string.Equals(field, "id", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(field, "source", StringComparison.OrdinalIgnoreCase);

    public static double TokenSimilarity(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right)
    {
        var leftTokens = left
            .Where(kvp => !IsNonCanonicalField(kvp.Key))
            .SelectMany(kvp => Tokens(kvp.Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rightTokens = right
            .Where(kvp => !IsNonCanonicalField(kvp.Key))
            .SelectMany(kvp => Tokens(kvp.Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
            return 0;

        var intersection = leftTokens.Intersect(rightTokens, StringComparer.OrdinalIgnoreCase).Count();
        var union = leftTokens.Union(rightTokens, StringComparer.OrdinalIgnoreCase).Count();
        return union == 0 ? 0 : (double)intersection / union;
    }

    public static bool SharedExact(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right, string field)
        => left.TryGetValue(field, out var leftValue) &&
           right.TryGetValue(field, out var rightValue) &&
           Normalize(leftValue) is { Length: > 0 } normalizedLeft &&
           string.Equals(normalizedLeft, Normalize(rightValue), StringComparison.OrdinalIgnoreCase);
}
