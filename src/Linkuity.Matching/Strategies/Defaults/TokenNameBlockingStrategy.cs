using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Last-token name blocking keys, selected by semantic type
/// (LastName/FullName/OrganizationName/ProductName) and the Blocking role rather than literal
/// field names. Emits "name:{lastToken}". Reproduces
/// FileMetadataStore.GenerateBlockingKeys' name keys for the person profile.
/// </summary>
public sealed class TokenNameBlockingStrategy : IBlockingStrategy
{
    public string Name => "token-name";

    private static bool IsNameType(SemanticFieldType type) => type is
        SemanticFieldType.LastName or SemanticFieldType.FullName or
        SemanticFieldType.OrganizationName or SemanticFieldType.ProductName;

    public IReadOnlyList<string> GenerateKeys(EntityRecord record, MatchingProfile profile)
    {
        var keys = new List<string>();
        foreach (var (_, value) in BlockingFields.Select(record, profile, IsNameType))
        {
            var token = MatchKey.Tokens(value).LastOrDefault();
            if (!string.IsNullOrWhiteSpace(token))
                keys.Add($"name:{token}");
        }
        return keys;
    }
}
