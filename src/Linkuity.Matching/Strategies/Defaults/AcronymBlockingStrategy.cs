using Linkuity.Core.Models;
using Linkuity.Matching.Canonicalization;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Acronym blocking for organization names, two rules meeting in the middle:
/// GENERATE — names with 2..6 tokens emit "acr:{initials}", computed from BOTH the
/// suffix-keeping form (the C in SBC comes from CORP) and the suffix-stripped canonical
/// form when different, since the acronym-coiner's convention is unknowable.
/// RECOGNIZE — each canonical token of 2..5 purely alphabetic characters is itself a
/// potential acronym ("SBC COMMUNICATIONS" -> acr:sbc). False collisions are bounded by
/// scoring (blocking only proposes) and by frequency suppression (generic acr keys are
/// capped like any other block).
/// </summary>
public sealed class AcronymBlockingStrategy : IBlockingStrategy
{
    private const int MaxInitials = 6;
    private const int MinRecognizedLength = 2;
    private const int MaxRecognizedLength = 5;

    private static readonly OrganizationNameCanonicalizer Canonicalizer = new();

    public string Name => "acronym";

    private static bool IsOrg(SemanticFieldType type) => type == SemanticFieldType.OrganizationName;

    public IReadOnlyList<string> GenerateKeys(EntityRecord record, MatchingProfile profile)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, value) in BlockingFields.Select(record, profile, IsOrg))
        {
            AddGeneratedInitials(keys, Canonicalizer.CanonicalizeKeepingSuffixes(value));

            var canonical = Canonicalizer.Canonicalize(value);
            AddGeneratedInitials(keys, canonical);

            foreach (var token in canonical)
                if (token.Length is >= MinRecognizedLength and <= MaxRecognizedLength && token.All(char.IsLetter))
                    keys.Add($"acr:{token.ToLowerInvariant()}");
        }
        return keys.ToList();
    }

    private static void AddGeneratedInitials(HashSet<string> keys, IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 2 || tokens.Count > MaxInitials)
            return;
        keys.Add($"acr:{string.Concat(tokens.Select(t => char.ToLowerInvariant(t[0])))}");
    }
}
