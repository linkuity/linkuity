using Linkuity.Core.Models;
using Linkuity.Matching.Canonicalization;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Rare-token blocking (the many-strict-keys model): one "token:{t}" key per canonical
/// token of every variant, minimum length 2, for fields whose semantic type has a
/// registered canonicalizer. Deliberately loose — common tokens form large blocks that
/// frequency-aware suppression (profile maxBlockSize) removes from candidacy, leaving
/// the rare, distinctive tokens to carry subset/reordered-name recall.
/// </summary>
public sealed class TokenBlockingStrategy : IBlockingStrategy
{
    private const int MinTokenLength = 2;

    public string Name => "token";

    public IReadOnlyList<string> GenerateKeys(EntityRecord record, MatchingProfile profile)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in profile.Fields)
        {
            if (!field.Roles.HasFlag(FieldRole.Blocking))
                continue;
            if (!TokenCanonicalizers.Default.TryGetValue(field.SemanticType, out var canonicalizer))
                continue;
            if (!record.Fields.TryGetValue(field.Name, out var value) || field.IsAbsent(value))
                continue;

            foreach (var variant in canonicalizer.Variants(value))
                foreach (var token in variant)
                    if (token.Length >= MinTokenLength)
                        keys.Add($"token:{token.ToLowerInvariant()}");
        }
        return keys.ToList();
    }
}
