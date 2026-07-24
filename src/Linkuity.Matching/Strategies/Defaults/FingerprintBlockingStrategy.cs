using Linkuity.Core.Models;
using Linkuity.Matching.Canonicalization;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Fingerprint blocking: one key per name built from the canonical tokens — sorted,
/// deduped, lowercased, space-joined — so word-order, article, punctuation, and
/// legal-suffix variants land in the same block. Semantic-type-driven: a field
/// participates only when its type has a registered canonicalizer (currently
/// OrganizationName). Emits "fp:{tokens}".
/// </summary>
public sealed class FingerprintBlockingStrategy : IBlockingStrategy
{
    private static readonly IReadOnlyDictionary<SemanticFieldType, ITokenCanonicalizer> Canonicalizers =
        new Dictionary<SemanticFieldType, ITokenCanonicalizer>
        {
            [SemanticFieldType.OrganizationName] = new OrganizationNameCanonicalizer()
        };

    public string Name => "fingerprint";

    public IReadOnlyList<string> GenerateKeys(EntityRecord record, MatchingProfile profile)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in profile.Fields)
        {
            if (!field.Roles.HasFlag(FieldRole.Blocking))
                continue;
            if (!Canonicalizers.TryGetValue(field.SemanticType, out var canonicalizer))
                continue;
            if (!record.Fields.TryGetValue(field.Name, out var value) || string.IsNullOrWhiteSpace(value))
                continue;

            var tokens = canonicalizer.Canonicalize(value);
            if (tokens.Count == 0)
                continue;

            var fingerprint = string.Join(' ', tokens
                .Select(t => t.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(t => t, StringComparer.Ordinal));
            keys.Add($"fp:{fingerprint}");
        }
        return keys.ToList();
    }
}
