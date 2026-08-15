using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Mdm.Resolution;

/// <summary>
/// The (merge-priority index, source-field name) pair every golden-record merge call site needs,
/// derived once so the three call sites that used to compute this independently can't drift apart
/// again — the same failure class F54 exists to prevent, one file over.
/// </summary>
internal static class MergePolicyResolver
{
    public static (IReadOnlyDictionary<string, string[]> MergeIndex, string SourceField) For(Project project, MatchingProfile profile)
    {
        var mergeIndex = project.MergeConfiguration?.MergeFields
            .ToDictionary(f => f.FieldName, f => f.SourcePriority, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var sourceField = profile.Fields
            .FirstOrDefault(f => f.SemanticType == SemanticFieldType.SourceIdentifier)?.Name ?? "source";
        return (mergeIndex, sourceField);
    }
}
