using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Exact-value blocking keys for identifier-like fields, selected by the Blocking
/// role and either an exact-value semantic type (Email/Phone/DomainName/DateOfBirth)
/// OR a profile-declared <see cref="FieldRole.Identifier"/>. Emits "{field}:{normalized}".
/// The role clause lets new identifier types (e.g. Sku/Gtin) block without an engine
/// change; existing exact-typed fields are unaffected.
/// </summary>
public sealed class ExactValueBlockingStrategy : IBlockingStrategy
{
    public string Name => "exact-value";

    private static bool IsExactType(SemanticFieldType type) => type is
        SemanticFieldType.Email or SemanticFieldType.Phone or
        SemanticFieldType.DomainName or SemanticFieldType.DateOfBirth;

    public IReadOnlyList<string> GenerateKeys(EntityRecord record, MatchingProfile profile)
    {
        var keys = new List<string>();
        foreach (var (name, value) in BlockingFields.Select(
                     record, profile, f => IsExactType(f.SemanticType) || f.Roles.HasFlag(FieldRole.Identifier)))
        {
            if (MatchKey.Normalize(value) is { Length: > 0 } normalized)
                keys.Add($"{name}:{normalized}");
        }
        return keys;
    }
}
