using Linkuity.Core.Models;

namespace Linkuity.Matching.Profiles;

/// <summary>
/// A record for the same reason <see cref="MatchingProfile"/> is: a variant should be derived
/// with <c>with</c> rather than by restating every property, because a hand-copy that omits one
/// silently changes matching rather than failing to compile.
/// </summary>
public sealed record ProfileField
{
    public required string Name { get; init; }
    public required SemanticFieldType SemanticType { get; init; }
    public required FieldRole Roles { get; init; }

    /// <summary>Name of the similarity evaluator for this field (consumed in Milestone 13).</summary>
    public string? SimilarityEvaluator { get; init; }

    /// <summary>Per-field scoring weight (consumed in Milestone 13). Defaults to 1.0.</summary>
    public double Weight { get; init; } = 1.0;

    /// <summary>
    /// Optional per-field evaluator configuration (consumed by the numeric, date,
    /// and n-gram evaluators). Documented keys: "numeric.tolerance",
    /// "numeric.maxPercentDiff", "date.maxDays", "ngram.size". Defaults to null.
    /// </summary>
    public IReadOnlyDictionary<string, string>? EvaluatorOptions { get; init; }

    /// <summary>
    /// What this field is worth to the evidence scorer. Required by that scorer for every
    /// Matchable field — it throws rather than inferring values from <see cref="Weight"/>,
    /// because inventing statistics is exactly what the evidence model exists to stop.
    /// Ignored by the older weighted scorers.
    /// </summary>
    public FieldEvidence? Evidence { get; init; }

    /// <summary>
    /// Fields sharing a non-null group are two spellings of the same fact and contribute ONCE,
    /// taking the strongest comparison among them. Averaging tolerated duplication; addition does
    /// not — the shipped person profile declares full_name and name at identical weight, which an
    /// additive model would count twice.
    /// </summary>
    public string? AliasGroup { get; init; }

    /// <summary>
    /// The field this one's value is DERIVED from, rather than read from a column of its own.
    /// Set together with <see cref="Extractor"/>; neither means anything without the other, and
    /// the loader rejects one without the other.
    ///
    /// A derived field exists so a fact carried inside another column can be scored as evidence
    /// in its own right. The organization legal form is the case this was built for: it lives in
    /// the name's trailing token, name canonicalization must strip it, and stripping is what lets
    /// two different companies in one corporate group at one address ("ABB AG", "ABB B.V.")
    /// compare as identical. Deriving it leaves name comparison untouched and gives the form its
    /// own measured weight — one fact in one place, scored once, which is the same rule that
    /// forbids scoring five nested location facets as five independent ones.
    ///
    /// Derived FROM the name rather than read from a separate legal-form column on purpose: a
    /// column can be unpopulated, and a name whose suffix differs always carries the fact. Where a
    /// profile has both available, it must declare only one of them Matchable, or the same fact is
    /// counted twice.
    /// </summary>
    public string? SourceField { get; init; }

    /// <summary>
    /// Name of the <see cref="Extraction.IValueExtractor"/> that turns
    /// <see cref="SourceField"/>'s value into this field's value. Resolved against
    /// <see cref="Extraction.ValueExtractors.Default"/> at profile load, so an unknown name fails
    /// when the profile is read rather than silently producing an empty field at match time.
    /// </summary>
    public string? Extractor { get; init; }

    /// <summary>True when this field's value is derived from another field rather than ingested.</summary>
    public bool IsDerived => SourceField is not null && Extractor is not null;

    /// <summary>
    /// Values that mean "absent" for this field even though the string itself is non-blank —
    /// GLEIF's legal-form code "8888" ("not provided") sitting on 10% of records, a national-ID
    /// placeholder like "000-00-0000", "UNKNOWN", "N/A". Declared per field because the same
    /// literal can be a real value on one field and a sentinel on another; nothing in the engine
    /// hard-codes any of these values, they are profile data. Compared case- and trim-insensitively
    /// via <see cref="IsAbsent"/>. Null or empty (the default) changes nothing: every existing
    /// profile that does not declare this behaves exactly as it did before the property existed.
    /// </summary>
    public IReadOnlyList<string>? NullEquivalents { get; init; }

    /// <summary>
    /// True when <paramref name="value"/> is blank, or matches a declared
    /// <see cref="NullEquivalents"/> entry case- and trim-insensitively. The single predicate every
    /// "does this record actually carry a value for this field" check in the engine must use, so a
    /// sentinel cannot slip past one caller (similarity comparison) while still being read as a real
    /// value by another (blocking-key generation) — either gap would let a sentinel be scored, or
    /// worse, would collapse every record sharing it into one blocking key.
    /// </summary>
    public bool IsAbsent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        if (NullEquivalents is not { Count: > 0 })
            return false;

        var trimmed = value.Trim();
        foreach (var sentinel in NullEquivalents)
            if (string.Equals(sentinel.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
