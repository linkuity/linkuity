using Linkuity.Core.Models;
using Linkuity.Matching.Extraction;
using Linkuity.Matching.Profiles;

namespace Linkuity.Matching.Strategies;

/// <summary>
/// The single seam every caller uses to turn a profile into the normalization it must apply.
///
/// Field derivation cannot live inside an <see cref="INormalizationStrategy"/>, because whether
/// derivation happens would then depend on which strategy a profile picked — and the shipped
/// organization profiles pick <c>identity</c>, which returns the record untouched. Derivation is a
/// property of the FIELDS a profile declares, not of how it normalizes, so it composes over
/// whichever strategy was chosen.
///
/// Callers must resolve through here rather than indexing the registry directly. There are nine
/// such call sites (the engine plus the audit, calibration and diagnostic services), and a record
/// that reaches a scorer without its derived fields compares as missing on them — silently, and
/// only for whichever path was missed. This is the same failure the ingest-time
/// <c>RecordNormalizer</c> seam exists to prevent, where batch and durable drifted apart and
/// blocking keys stopped matching.
/// </summary>
public static class ProfileNormalization
{
    /// <summary>
    /// The profile's declared normalization strategy, wrapped to add derived fields when the
    /// profile declares any. A profile with no derived fields gets the strategy back unchanged —
    /// same instance, same behaviour, nothing to review.
    /// </summary>
    public static INormalizationStrategy Resolve(IStrategyRegistry registry, MatchingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(profile);

        var inner = registry.Normalization[profile.NormalizationStrategy];
        var derived = profile.Fields.Where(f => f.IsDerived).ToArray();
        return derived.Length == 0 ? inner : new DerivedFieldNormalizationStrategy(inner, derived, profile);
    }

    /// <summary>
    /// Runs the wrapped strategy, then writes each derived field from its source field.
    /// Reports the wrapped strategy's <see cref="INormalizationStrategy.Name"/> as its own: this
    /// is the profile's normalization with derivation applied, not a different strategy, and
    /// anything recording which strategy ran should record the one the profile declared.
    /// </summary>
    private sealed class DerivedFieldNormalizationStrategy(
        INormalizationStrategy inner,
        IReadOnlyList<ProfileField> derivedFields,
        MatchingProfile profile) : INormalizationStrategy
    {
        private readonly IReadOnlyDictionary<string, ProfileField> _byName =
            profile.Fields.ToDictionary(f => f.Name, f => f, StringComparer.OrdinalIgnoreCase);

        public string Name => inner.Name;

        public EntityRecord Normalize(EntityRecord record, MatchingProfile matchingProfile)
        {
            var normalized = inner.Normalize(record, matchingProfile);

            // Preserve the inner strategy's key comparer. identity returns the ingested
            // dictionary and semantic-field builds an ordinal one; forcing either to the other
            // would change which field names resolve, for every field, not just derived ones.
            var comparer = normalized.Fields is Dictionary<string, string> d
                ? d.Comparer
                : StringComparer.Ordinal;
            var fields = new Dictionary<string, string>(normalized.Fields, comparer);

            foreach (var field in derivedFields)
            {
                // Extraction reads the value AFTER the inner strategy has normalized it, so the
                // derived value is a function of what the rest of the engine will compare.
                var sourceValue = fields.TryGetValue(field.SourceField!, out var v) ? v : null;

                // A sentinel source ("name under confirmation") carries no legal form. Asking the
                // SOURCE field whether its own value is absent is what keeps that judgement in one
                // place, rather than re-deciding it here with a different rule.
                var sourceField = _byName.GetValueOrDefault(field.SourceField!);
                var absent = sourceField?.IsAbsent(sourceValue) ?? string.IsNullOrWhiteSpace(sourceValue);

                // Written unconditionally, including to empty: a derived field is authoritative
                // over any same-named column that happened to be ingested, and leaving a stale
                // ingested value in place would score a fact the profile said to derive.
                fields[field.Name] = absent
                    ? string.Empty
                    : ValueExtractors.Default[field.Extractor!].Extract(sourceValue!);
            }

            return normalized with { Fields = fields };
        }
    }
}
