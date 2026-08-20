using System.Globalization;
using System.Text;
using Linkuity.Core.Models;

namespace Linkuity.Core.Normalization;

public static class FieldNormalizer
{
    /// <summary>
    /// Region assumed for phone numbers written without a country code. US by default because
    /// that is what this previously hardcoded; it is a default, not a correct answer, and any
    /// non-US deployment should set it on the profile.
    /// </summary>
    public const string DefaultPhoneRegion = "US";

    /// <summary>
    /// Both punctuated and bare forms: "Mr Smith" is at least as common in real data as
    /// "Mr. Smith", and only the punctuated variants were recognised. Stripping is guarded by a
    /// following-whitespace check, so "Drew" keeps its "Dr" and "Mission" keeps its "Miss".
    /// </summary>
    private static readonly string[] Honorifics =
    [
        "Mr.", "Mrs.", "Ms.", "Dr.", "Prof.",
        "Mr", "Mrs", "Ms", "Miss", "Dr", "Prof"
    ];

    /// <summary>
    /// Unambiguous formats, read identically under either date order.
    /// </summary>
    private static readonly string[] UnambiguousDateFormats =
    [
        "yyyy-MM-dd",
        "yyyy/MM/dd"
    ];

    private static readonly string[] MonthFirstDateFormats =
    [
        .. UnambiguousDateFormats,
        "MM/dd/yyyy",
        "M/d/yyyy",
        "MMM d yyyy",
        "MMMM d yyyy"
    ];

    private static readonly string[] DayFirstDateFormats =
    [
        .. UnambiguousDateFormats,
        "dd/MM/yyyy",
        "d/M/yyyy",
        "d MMM yyyy",
        "d MMMM yyyy"
    ];

    public static string Normalize(
        string value,
        SemanticFieldType type,
        string phoneRegion = DefaultPhoneRegion,
        DateFieldOrder dateOrder = DateFieldOrder.MonthFirst)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        // Before anything type-specific, so every rule below and every value that reaches storage,
        // blocking and comparison is in one Unicode form. See ToCanonicalForm.
        value = ToCanonicalForm(value);

        return type switch
        {
            SemanticFieldType.Email => value.Trim().ToLowerInvariant(),
            SemanticFieldType.DomainName => value.Trim().ToLowerInvariant(),
            SemanticFieldType.Phone => PhoneNormalizer.Normalize(value, phoneRegion) ?? value,
            SemanticFieldType.DateOfBirth => NormalizeDate(value, dateOrder),
            SemanticFieldType.FirstName or SemanticFieldType.LastName or SemanticFieldType.FullName
                => StripHonorific(value),
            SemanticFieldType.AddressLine or SemanticFieldType.PostalCode or SemanticFieldType.OrganizationName
                => value.Trim(),
            // ISO 3166 (Country, Jurisdiction), ISO 3166-2 (Region) and ELF (LegalForm) are all
            // conventionally upper-case codes; City is free text but the corpora that carry it
            // alongside these already arrive upper-cased, so folding it the same way keeps one
            // rule for the group instead of a special case. No alias/lookup translation here —
            // that is a separate concern.
            SemanticFieldType.City or SemanticFieldType.Region or SemanticFieldType.Country or
                SemanticFieldType.Jurisdiction or SemanticFieldType.LegalForm
                => value.Trim().ToUpperInvariant(),
            // No normalization rule of its own (yet): passed through unchanged, same as before
            // this switch's catch-all was removed, so behaviour is unchanged but now explicit.
            SemanticFieldType.SourceIdentifier or SemanticFieldType.Sku or SemanticFieldType.Gtin or
                SemanticFieldType.ProductName
                => value,
            // C# still requires a discard arm to cover values outside the declared enum (an
            // arbitrary int cast to SemanticFieldType), even once every named member above has an
            // explicit case. Unlike the catch-all this replaces, it does not silently pass the
            // value through: it throws, so a semantic type reaching here — whether an out-of-range
            // cast or, deliberately, a newly declared enum member nobody gave a case yet — fails
            // loudly instead of being read as normalized when it never was.
            _ => throw new ArgumentOutOfRangeException(
                nameof(type), type, $"FieldNormalizer.Normalize has no case for {type}.")
        };
    }

    /// <summary>
    /// Canonical composition (NFC). A character can be written as one code point or as a base
    /// letter plus a combining mark; Unicode calls those canonically equivalent, but they are
    /// different strings and nothing else in the pipeline reconciled them. Two sources that
    /// disagree about which form they emit produced records that never matched, and because
    /// <c>MatchKey.Normalize</c> keeps only letters and digits, the decomposed spelling quietly
    /// lost its accent while the composed one kept it — so the loss was invisible and landed on
    /// exactly the non-English data F12 is about.
    ///
    /// NFC, deliberately, and never NFKC. Canonical composition discards nothing: the two forms
    /// are the same text by definition, so choosing one is a representation decision rather than
    /// a judgement about meaning. Compatibility composition would additionally fold a ligature
    /// into its letters and a circled digit into a plain one, deciding that two genuinely
    /// different source values are one — which is what F62 forbids.
    ///
    /// This is not accent folding, and does not make an accented name match its unaccented
    /// spelling. It previously did so by accident for decomposed input only; that inconsistency
    /// is what is being removed. Whether matching *should* fold accents is a separate question,
    /// open under F12.
    /// </summary>
    private static string ToCanonicalForm(string value)
    {
        try
        {
            return value.IsNormalized(NormalizationForm.FormC)
                ? value
                : value.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // Invalid Unicode — an unpaired surrogate, say — cannot be normalized. Keep the raw
            // value rather than let an ingest die on one malformed cell: the same choice this
            // class already makes for an unparseable date or phone number.
            return value;
        }
    }

    private static string NormalizeDate(string value, DateFieldOrder order)
    {
        var formats = order == DateFieldOrder.DayFirst ? DayFirstDateFormats : MonthFirstDateFormats;
        if (DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
            return date.ToString("yyyy-MM-dd");

        // Unparseable dates pass through rather than becoming empty: the raw value is still
        // comparable to an identically-written one, where a blank is comparable to nothing.
        return value;
    }

    private static string StripHonorific(string value)
    {
        var trimmed = value.TrimStart();
        foreach (var honorific in Honorifics)
        {
            if (trimmed.Length <= honorific.Length)
                continue;
            if (!trimmed.StartsWith(honorific, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!char.IsWhiteSpace(trimmed[honorific.Length]))
                continue;
            return trimmed[(honorific.Length)..].Trim();
        }
        return trimmed;
    }
}
