using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Prefix blocking: the first N normalized characters of name/text fields,
/// selected by semantic type and the Blocking role. Groups records whose names
/// share a leading stem. Emits "prefix:{stem}".
/// </summary>
public sealed class PrefixBlockingStrategy : IBlockingStrategy
{
    private readonly int _prefixLength;

    public PrefixBlockingStrategy(int prefixLength = 4)
    {
        if (prefixLength < 1)
            throw new ArgumentOutOfRangeException(nameof(prefixLength), "Prefix length must be at least 1.");
        _prefixLength = prefixLength;
    }

    public string Name => "prefix";

    private static bool IsTextType(SemanticFieldType type) => type is
        SemanticFieldType.FirstName or SemanticFieldType.LastName or
        SemanticFieldType.FullName or SemanticFieldType.OrganizationName;

    public IReadOnlyList<string> GenerateKeys(EntityRecord record, MatchingProfile profile)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, value) in BlockingFields.Select(record, profile, IsTextType))
        {
            var normalized = MatchKey.Normalize(value);
            if (normalized.Length == 0)
                continue;
            var stem = normalized.Length <= _prefixLength ? normalized : normalized[.._prefixLength];
            keys.Add($"prefix:{stem}");
        }
        return keys.ToList();
    }
}
