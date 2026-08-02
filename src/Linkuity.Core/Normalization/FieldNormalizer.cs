using System.Globalization;
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
            _ => value
        };
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
