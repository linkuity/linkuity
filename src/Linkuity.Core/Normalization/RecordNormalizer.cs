using Linkuity.Core.Models;

namespace Linkuity.Core.Normalization;

/// <summary>
/// Applies <see cref="FieldNormalizer"/> across a record's fields, given a map from field
/// name to semantic type. This is the single ingest-time normalization seam: the batch
/// pipeline and durable ingest both call it, so the two paths cannot drift into normalizing
/// differently.
///
/// They previously could, and did. Batch normalized on the way in and durable did not, which
/// left the same entity carrying a different canonical value depending on which path loaded
/// it, and — because blocking keys are built from stored values — made durable fail to
/// retrieve pairs batch merged.
///
/// Field names are matched case-insensitively, matching how profiles resolve fields
/// elsewhere. Unmapped fields pass through untouched, so a CSV column the profile does not
/// describe is preserved rather than silently dropped or altered.
/// </summary>
public static class RecordNormalizer
{
    /// <summary>Normalizes one value, or returns it unchanged when the field is unmapped.</summary>
    public static string NormalizeValue(
        string field, string value, IReadOnlyDictionary<string, SemanticFieldType> fieldMap)
    {
        ArgumentNullException.ThrowIfNull(fieldMap);
        return fieldMap.TryGetValue(field, out var type) ? FieldNormalizer.Normalize(value, type) : value;
    }

    /// <summary>
    /// Normalizes every mapped field, preserving the dictionary's comparer so callers do not
    /// silently lose case-insensitive field lookup.
    /// </summary>
    public static Dictionary<string, string> NormalizeFields(
        IReadOnlyDictionary<string, string> fields, IReadOnlyDictionary<string, SemanticFieldType> fieldMap)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(fieldMap);

        var normalized = new Dictionary<string, string>(fields.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (field, value) in fields)
            normalized[field] = NormalizeValue(field, value, fieldMap);
        return normalized;
    }
}
