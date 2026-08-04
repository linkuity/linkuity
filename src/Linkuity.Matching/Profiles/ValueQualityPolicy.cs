namespace Linkuity.Matching.Profiles;

/// <summary>
/// Decides whether a value may earn RARITY-derived evidence. It never removes a field's ordinary
/// agreement evidence — the failure being guarded against is specific: a mis-keyed value shared by
/// two records is rare BECAUSE it is wrong, so rarity weighting turns bad data into strong
/// evidence unless something stops it.
/// <para>
/// Generic high-frequency values need nothing here: high frequency is high u, which the evidence
/// model already handles. This is only for values that are rare and worthless.
/// </para>
/// <para>
/// Ships inert. Rarity arrives in stage 2; nothing in stage 1a consults this.
/// </para>
/// </summary>
public sealed class ValueQualityPolicy
{
    private readonly HashSet<string> _placeholders;

    public ValueQualityPolicy(IEnumerable<string> placeholderValues)
    {
        ArgumentNullException.ThrowIfNull(placeholderValues);
        _placeholders = new HashSet<string>(
            placeholderValues.Select(v => v.Trim()), StringComparer.OrdinalIgnoreCase);
    }

    public bool IsRarityEligible(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (_placeholders.Contains(trimmed))
            return false;

        return HasMoreThanOneDistinctAlphanumeric(trimmed);
    }

    /// <summary>
    /// "000-000-0000" and "XXXXXX" carry one distinct character between them. Separators are
    /// ignored so a padded identifier is judged on its content rather than its punctuation.
    /// </summary>
    private static bool HasMoreThanOneDistinctAlphanumeric(string value)
    {
        var seen = new HashSet<char>();
        foreach (var c in value)
            if (char.IsLetterOrDigit(c))
            {
                seen.Add(char.ToUpperInvariant(c));
                if (seen.Count > 1) return true;
            }
        return false;
    }
}
