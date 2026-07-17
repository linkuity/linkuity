using Linkuity.Core.Models;
using Linkuity.Matching.Phonetics;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Phonetic blocking via Double Metaphone, selected by semantic type and the
/// Blocking role, so spelling variants of a name collapse to a shared key. The
/// token chosen per field mirrors the Python double-metaphone path: last token of
/// a full name, first non-stopword token of an organization name, the whole value
/// of a last name. Emits "phonetic:{primary}" and "phonetic:{alternate}".
/// </summary>
public sealed class PhoneticBlockingStrategy : IBlockingStrategy
{
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase) { "the", "a", "an" };

    public string Name => "phonetic";

    private static bool IsNameType(SemanticFieldType type) => type is
        SemanticFieldType.LastName or SemanticFieldType.FullName or
        SemanticFieldType.OrganizationName;

    public IReadOnlyList<string> GenerateKeys(EntityRecord record, MatchingProfile profile)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in profile.Fields)
        {
            if (!field.Roles.HasFlag(FieldRole.Blocking) || !IsNameType(field.SemanticType))
                continue;
            if (!record.Fields.TryGetValue(field.Name, out var value) || string.IsNullOrWhiteSpace(value))
                continue;

            var token = SelectToken(value, field.SemanticType);
            if (string.IsNullOrEmpty(token))
                continue;

            var (primary, alternate) = DoubleMetaphone.Encode(token);
            if (!string.IsNullOrEmpty(primary))
                keys.Add($"phonetic:{primary}");
            if (!string.IsNullOrEmpty(alternate) && alternate != primary)
                keys.Add($"phonetic:{alternate}");
        }
        return keys.ToList();
    }

    private static string SelectToken(string value, SemanticFieldType type)
    {
        var tokens = MatchKey.Tokens(value).ToList();
        if (tokens.Count == 0)
            return "";

        return type switch
        {
            SemanticFieldType.FullName => tokens[^1],
            SemanticFieldType.OrganizationName => tokens.FirstOrDefault(t => !Stopwords.Contains(t)) ?? tokens[0],
            _ => tokens[0]
        };
    }
}
