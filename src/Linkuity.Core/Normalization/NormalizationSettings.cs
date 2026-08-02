using Linkuity.Core.Models;

namespace Linkuity.Core.Normalization;

/// <summary>
/// Everything ingest-time normalization needs from a profile, without Core having to know what
/// a profile is.
/// </summary>
/// <param name="FieldMap">
/// Field name to semantic type. Unmapped fields are left untouched.
/// </param>
/// <param name="PhoneRegion">
/// Two-letter region code used to interpret phone numbers written without a country code, which
/// are otherwise ambiguous. Defaults to <c>US</c>, preserving the behaviour this replaced. A
/// number carrying an explicit country code ignores this entirely.
/// </param>
/// <param name="DateOrder">
/// How to read a slash-separated date whose first two components are ambiguous. Defaults to
/// month-first, preserving the behaviour this replaced. See <see cref="DateFieldOrder"/>.
/// </param>
public sealed record NormalizationSettings(
    IReadOnlyDictionary<string, SemanticFieldType> FieldMap,
    string PhoneRegion = FieldNormalizer.DefaultPhoneRegion,
    DateFieldOrder DateOrder = DateFieldOrder.MonthFirst);
