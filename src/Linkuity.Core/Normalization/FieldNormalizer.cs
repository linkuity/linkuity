using System.Globalization;
using Linkuity.Core.Models;

namespace Linkuity.Core.Normalization;

public static class FieldNormalizer
{
    private static readonly string[] Honorifics = ["Mr.", "Mrs.", "Ms.", "Miss", "Dr.", "Prof."];

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd",
        "yyyy/MM/dd",
        "MM/dd/yyyy",
        "M/d/yyyy",
        "MMM d yyyy",
        "MMMM d yyyy"
    ];

    public static string Normalize(string value, SemanticFieldType type)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return type switch
        {
            SemanticFieldType.Email => value.Trim().ToLowerInvariant(),
            SemanticFieldType.DomainName => value.Trim().ToLowerInvariant(),
            SemanticFieldType.Phone => PhoneNormalizer.Normalize(value) ?? value,
            SemanticFieldType.DateOfBirth => NormalizeDate(value),
            SemanticFieldType.FirstName or SemanticFieldType.LastName or SemanticFieldType.FullName
                => StripHonorific(value),
            SemanticFieldType.AddressLine or SemanticFieldType.PostalCode or SemanticFieldType.OrganizationName
                => value.Trim(),
            _ => value
        };
    }

    private static string NormalizeDate(string value)
    {
        if (DateTime.TryParseExact(value.Trim(), DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
            return date.ToString("yyyy-MM-dd");
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
