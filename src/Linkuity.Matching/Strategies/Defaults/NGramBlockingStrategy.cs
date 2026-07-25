using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// N-gram blocking: distinct character n-grams of name/text fields, selected by
/// semantic type and the Blocking role. Groups records that share substrings even
/// when full tokens differ. Grams are generated PER TOKEN: word boundaries are never
/// spanned, since a gram straddling two tokens (e.g. the tail of one word concatenated
/// with the head of the next) is meaningless and only creates false candidate pairs.
/// Tokens no longer than n emit themselves whole. Emits "ngram:{gram}".
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
            foreach (var token in MatchKey.Tokens(value))
            {
                if (token.Length == 0)
                    continue;
                if (token.Length <= _n)
                {
                    keys.Add($"ngram:{token}");
                    continue;
                }
                for (var i = 0; i + _n <= token.Length; i++)
                    keys.Add($"ngram:{token.Substring(i, _n)}");
            }
        }
        return keys.ToList();
    }
}
