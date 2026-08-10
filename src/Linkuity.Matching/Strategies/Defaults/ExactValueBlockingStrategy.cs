using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Exact-value blocking keys for identifier-like fields, selected by the Blocking
/// role and either an exact-value semantic type (Email/Phone/DomainName/DateOfBirth/
/// PostalCode) OR a profile-declared <see cref="FieldRole.Identifier"/>. Emits
/// "{field}:{normalized}". The role clause lets new identifier types (e.g. Sku/Gtin)
/// block without an engine change; existing exact-typed fields are unaffected.
/// PostalCode is an exact-value semantic type deliberately, not via the Identifier
/// role: declaring a field Identifier also floors <see cref="IdentifierAwareWeightedScoringStrategy"/>
/// to auto-merge (0.98) whenever the weighted similarity clears the identifier
/// corroboration gate. A shared postcode is reachability evidence — measured to recover
/// 107,204 otherwise-unreachable true pairs on the GLEIF org corpus — but it is not
/// uniqueness evidence: a registration-agent address (e.g. Wilmington, DE 19801) is
/// shared by thousands of unrelated companies, so flooring on it would auto-merge them.
/// Country/Jurisdiction/LegalForm are NOT included: measured cardinality on the same
/// corpus (236/308/2,476 distinct values, with single values covering hundreds of
/// thousands of records) means every meaningful key those fields would emit is discarded
/// by maxBlockSize — pure cost, no recall benefit.
/// </summary>
public sealed class ExactValueBlockingStrategy : IBlockingStrategy
{
    public string Name => "exact-value";

    private static bool IsExactType(SemanticFieldType type) => type is
        SemanticFieldType.Email or SemanticFieldType.Phone or
        SemanticFieldType.DomainName or SemanticFieldType.DateOfBirth or
        SemanticFieldType.PostalCode;

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
