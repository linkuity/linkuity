using Linkuity.Core.Merge;
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
        // sourceField unknown means source-priority has nothing to key on, so every field
        // resolves by consensus regardless of what the merge config asks for.
        var mergeIndex = mergeConfig is not null && sourceField is not null
            ? mergeConfig.MergeFields.ToDictionary(f => f.FieldName, f => f.SourcePriority, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        return clusters.Select(cluster =>
        {
            var members = cluster
                .Where(recordsById.ContainsKey)
                .Select(id => recordsById[id])
                .ToList();

            var fields = GoldenRecordMerge.MergeFields(members, mergeIndex, sourceField ?? "");

            return new GoldenRecord
            {
                ClusterId = Guid.NewGuid(),
                MemberIds = cluster.ToList(),
                Fields = fields
            };
        }).ToList();
    }
}
