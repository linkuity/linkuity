using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// N-gram blocking: distinct character n-grams of name/text fields, selected by
/// semantic type and the Blocking role. Groups records that share substrings even
/// when full tokens differ. Emits "ngram:{gram}".
/// </summary>
public sealed class NGramBlockingStrategy : IBlockingStrategy
{
    private readonly int _n;

    public NGramBlockingStrategy(int n = 3)
    {
        if (n < 1)
            throw new ArgumentOutOfRangeException(nameof(n), "N-gram size must be at least 1.");
        _n = n;
    }

    public string Name => "ngram";

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
            if (normalized.Length <= _n)
            {
                keys.Add($"ngram:{normalized}");
                continue;
            }
            for (var i = 0; i + _n <= normalized.Length; i++)
                keys.Add($"ngram:{normalized.Substring(i, _n)}");
        }
        return keys.ToList();
    }
}
