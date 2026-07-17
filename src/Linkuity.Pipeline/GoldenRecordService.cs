using Linkuity.Core.Models;

namespace Linkuity.Pipeline;

public class GoldenRecordService
{
    public IReadOnlyList<GoldenRecord> Merge(
        IReadOnlyList<IReadOnlyList<string>> clusters,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> recordsById,
        MergeConfiguration? mergeConfig,
        string? sourceField)
    {
        var mergeIndex = mergeConfig?.MergeFields
            .ToDictionary(f => f.FieldName, f => f.SourcePriority)
            ?? new Dictionary<string, string[]>();

        var allFields = recordsById.Values
            .SelectMany(r => r.Keys)
            .Distinct()
            .Where(f => f != "id" && f != sourceField)
            .ToList();

        return clusters.Select(cluster =>
        {
            var members = cluster
                .Where(recordsById.ContainsKey)
                .Select(id => recordsById[id])
                .ToList();

            var fields = new Dictionary<string, string>();
            foreach (var field in allFields)
            {
                fields[field] = mergeIndex.TryGetValue(field, out var priority) && sourceField != null
                    ? MergeByPriority(members, field, sourceField, priority)
                    : MergeByConsensus(members, field);
            }

            return new GoldenRecord
            {
                ClusterId = Guid.NewGuid(),
                MemberIds = cluster.ToList(),
                Fields = fields
            };
        }).ToList();
    }

    private static string MergeByPriority(
        List<IReadOnlyDictionary<string, string>> members,
        string field,
        string sourceField,
        string[] sourcePriority)
    {
        foreach (var source in sourcePriority)
        {
            var value = members
                .Where(r => r.TryGetValue(sourceField, out var s) && s == source)
                .Select(r => r.TryGetValue(field, out var v) ? v : "")
                .FirstOrDefault(v => !string.IsNullOrEmpty(v));
            if (value != null) return value;
        }
        return MergeByConsensus(members, field);
    }

    private static string MergeByConsensus(
        List<IReadOnlyDictionary<string, string>> members,
        string field)
    {
        return members
            .Select(r => r.TryGetValue(field, out var v) ? v : "")
            .Where(v => !string.IsNullOrEmpty(v))
            .GroupBy(v => v)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Key.Length)
            .FirstOrDefault()?.Key ?? "";
    }
}
