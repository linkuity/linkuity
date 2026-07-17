using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Selects the fields a blocking strategy operates on from profile + semantic
/// metadata rather than literal field names: a field participates when it has the
/// <see cref="FieldRole.Blocking"/> role, its semantic type satisfies the
/// strategy's predicate, and the record carries a non-empty value for it.
/// </summary>
internal static class BlockingFields
{
    public static IEnumerable<(string Name, string Value)> Select(
        EntityRecord record, MatchingProfile profile, Func<SemanticFieldType, bool> applies)
    {
        foreach (var field in profile.Fields)
        {
            if (!field.Roles.HasFlag(FieldRole.Blocking))
                continue;
            if (!applies(field.SemanticType))
                continue;
            if (record.Fields.TryGetValue(field.Name, out var value) && !string.IsNullOrWhiteSpace(value))
                yield return (field.Name, value);
        }
    }

    /// <summary>
    /// Overload selecting by a full <see cref="ProfileField"/> predicate, so a strategy
    /// can combine semantic type and role (e.g. exact-value also keying declared
    /// identifiers). The Blocking role and non-empty value checks are unchanged.
    /// </summary>
    public static IEnumerable<(string Name, string Value)> Select(
        EntityRecord record, MatchingProfile profile, Func<ProfileField, bool> applies)
    {
        foreach (var field in profile.Fields)
        {
            if (!field.Roles.HasFlag(FieldRole.Blocking))
                continue;
            if (!applies(field))
                continue;
            if (record.Fields.TryGetValue(field.Name, out var value) && !string.IsNullOrWhiteSpace(value))
                yield return (field.Name, value);
        }
    }
}
