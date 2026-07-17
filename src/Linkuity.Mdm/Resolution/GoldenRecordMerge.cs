using Linkuity.Core.Models;

namespace Linkuity.Mdm.Resolution;

internal static class GoldenRecordMerge
{
    internal static IReadOnlyDictionary<string, string> MergeFields(IReadOnlyList<EntityRecord> members)
        => MergeFields(new Project
        {
            Id = members.FirstOrDefault()?.ProjectId ?? Guid.Empty,
            Name = "",
            ContentType = "",
            CreatedAt = DateTimeOffset.MinValue
        }, members);

    internal static IReadOnlyDictionary<string, string> MergeFields(Project project, IReadOnlyList<EntityRecord> members)
    {
        var mergeIndex = project.MergeConfiguration?.MergeFields
            .ToDictionary(field => field.FieldName, field => field.SourcePriority, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var fields = members
            .SelectMany(r => r.Fields.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(field => !IsNonCanonicalField(field))
            .ToList();
        return fields.ToDictionary(
            field => field,
            field => mergeIndex.TryGetValue(field, out var sourcePriority)
                ? MergeByPriority(members, field, sourcePriority)
                : MergeByConsensus(members, field),
            StringComparer.OrdinalIgnoreCase);
    }

    internal static string MergeByPriority(IReadOnlyList<EntityRecord> members, string field, IReadOnlyList<string> sourcePriority)
    {
        foreach (var source in sourcePriority)
        {
            var value = members
                .Where(record => record.Fields.TryGetValue("source", out var recordSource) &&
                                 string.Equals(recordSource, source, StringComparison.OrdinalIgnoreCase))
                .Select(record => record.Fields.TryGetValue(field, out var fieldValue) ? fieldValue : "")
                .FirstOrDefault(fieldValue => !string.IsNullOrWhiteSpace(fieldValue));
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return MergeByConsensus(members, field);
    }

    internal static string MergeByConsensus(IReadOnlyList<EntityRecord> members, string field)
        => members
            .Select(r => r.Fields.TryGetValue(field, out var value) ? value : "")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.Key.Length)
            .FirstOrDefault()?.Key ?? "";

    internal static bool DictionaryEquals(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right)
        => left.Count == right.Count &&
           left.All(kvp => right.TryGetValue(kvp.Key, out var value) && string.Equals(kvp.Value, value, StringComparison.Ordinal));

    internal static bool IsNonCanonicalField(string field)
        => string.Equals(field, "id", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(field, "source", StringComparison.OrdinalIgnoreCase);
}
