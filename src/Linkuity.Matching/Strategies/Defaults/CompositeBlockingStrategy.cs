using Linkuity.Core.Models;
using Linkuity.Matching.Phonetics;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Composite blocking: combines several single-field "parts" into one tighter key
/// (the cross-product of part values joined by '+'), only when every part is
/// present. Mirrors the Python combined block (date of birth + phonetic last
/// name). Parts are profile/semantic-driven via <see cref="BlockingFields"/>.
/// </summary>
public sealed class CompositeBlockingStrategy : IBlockingStrategy
{
    private readonly IReadOnlyList<Func<EntityRecord, MatchingProfile, IReadOnlyList<string>>> _parts;

    public CompositeBlockingStrategy(string name, IReadOnlyList<Func<EntityRecord, MatchingProfile, IReadOnlyList<string>>> parts)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Composite strategy name must be non-empty.", nameof(name));
        if (parts is null || parts.Count == 0)
            throw new ArgumentException("Composite strategy requires at least one part.", nameof(parts));
        Name = name;
        _parts = parts;
    }

    public string Name { get; }

    public IReadOnlyList<string> GenerateKeys(EntityRecord record, MatchingProfile profile)
    {
        var partValues = new List<IReadOnlyList<string>>(_parts.Count);
        foreach (var part in _parts)
        {
            var values = part(record, profile);
            if (values.Count == 0)
                return [];
            partValues.Add(values);
        }

        IEnumerable<string> combos = [""];
        foreach (var values in partValues)
        {
            combos = combos.SelectMany(prefix => values.Select(v => prefix.Length == 0 ? v : $"{prefix}+{v}"));
        }

        return combos.Select(combo => $"{Name}:{combo}").Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The concrete default: date of birth + Double-Metaphone-primary of last name.</summary>
    public static CompositeBlockingStrategy DobLastNamePhonetic()
        => new("dob-lastname-phonetic",
        [
            ExactPart(SemanticFieldType.DateOfBirth),
            PhoneticPrimaryPart(SemanticFieldType.LastName)
        ]);

    private static Func<EntityRecord, MatchingProfile, IReadOnlyList<string>> ExactPart(SemanticFieldType type)
        => (record, profile) => BlockingFields.Select(record, profile, t => t == type)
            .Select(f => MatchKey.Normalize(f.Value))
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static Func<EntityRecord, MatchingProfile, IReadOnlyList<string>> PhoneticPrimaryPart(SemanticFieldType type)
        => (record, profile) => BlockingFields.Select(record, profile, t => t == type)
            .Select(f => DoubleMetaphone.Encode(f.Value).Primary)
            .Where(v => !string.IsNullOrEmpty(v))
            .Distinct(StringComparer.Ordinal)
            .ToList();
}
