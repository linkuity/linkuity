namespace Linkuity.Core.Merge;

/// <summary>
/// Golden-record field survivorship: the one implementation shared by the batch (<c>run</c>,
/// <c>POST /run</c>) and durable (<c>ingest-incremental</c>, <c>persist-batch</c>) paths. Both
/// used to carry their own copy of this logic (<c>Linkuity.Pipeline.GoldenRecordService</c> and
/// <c>Linkuity.Mdm.Resolution.GoldenRecordMerge</c>), and the copies had quietly drifted apart —
/// case-sensitive vs. case-insensitive consensus grouping, corpus-wide vs. cluster-local field
/// sets, a hardcoded vs. configurable source-field name, and a value-selection tie that depended
/// on the order members happened to be enumerated in. That last one is a correctness bug on its
/// own (F54: the same data loaded in a different order must produce the same golden record), so
/// every tie here is broken by field content, never by position in <c>members</c>.
/// </summary>
public static class GoldenRecordMerge
{
    public static IReadOnlyDictionary<string, string> MergeFields(
        IReadOnlyList<IReadOnlyDictionary<string, string>> members,
        IReadOnlyDictionary<string, string[]> sourcePriorityByField,
        string sourceField)
    {
        var fields = members
            .SelectMany(m => m.Keys)
            .GroupBy(field => field, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(v => v, StringComparer.Ordinal).First())
            .Where(field => !IsNonCanonicalField(field, sourceField))
            // #77: sorted by the canonical field name so the output's key order depends only on
            // field content, never on which order members happened to be enumerated in — the
            // same F54 reasoning this class's tie-breaks already apply, extended to the shape of
            // the returned dictionary itself.
            .OrderBy(field => field, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return fields.ToDictionary(
            field => field,
            field => sourcePriorityByField.TryGetValue(field, out var sourcePriority)
                ? MergeByPriority(members, field, sourceField, sourcePriority)
                : MergeByConsensus(members, field),
            StringComparer.OrdinalIgnoreCase);
    }

    public static string MergeByPriority(
        IReadOnlyList<IReadOnlyDictionary<string, string>> members,
        string field,
        string sourceField,
        IReadOnlyList<string> sourcePriority)
    {
        foreach (var source in sourcePriority)
        {
            var tier = members
                .Where(m => TryGetFieldValue(m, sourceField, out var s) &&
                            string.Equals(s, source, StringComparison.OrdinalIgnoreCase))
                .ToList();
            // Multiple members can share this tier's source and still disagree on the field's
            // value; resolve that disagreement the same way a whole-cluster tie resolves
            // (majority, then longest, then alphabetical) instead of taking whichever value
            // happened to be enumerated first.
            var value = MergeByConsensus(tier, field);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return MergeByConsensus(members, field);
    }

    public static string MergeByConsensus(IReadOnlyList<IReadOnlyDictionary<string, string>> members, string field)
        => members
            .Select(m => TryGetFieldValue(m, field, out var value) ? value : "")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.Key.Length)
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()
            ?.OrderBy(value => value, StringComparer.Ordinal)
            .First() ?? "";

    /// <summary>
    /// #76: a case-insensitive field lookup that works regardless of the comparer the member's
    /// own dictionary happens to use. MergeFields picks ONE canonical casing for a field name
    /// across all members, and that casing does not necessarily match any given member's own key
    /// casing. A plain <c>TryGetValue</c> only finds it when the member's dictionary is itself
    /// case-insensitive — true for a record built fresh in-process, but not for one reloaded via
    /// System.Text.Json (its dictionary deserializer defaults to the case-SENSITIVE ordinal
    /// comparer regardless of what comparer the object had before serialization). Relying on the
    /// caller's comparer silently drops that member's value from the merge; looking it up
    /// case-insensitively here does not.
    /// </summary>
    private static bool TryGetFieldValue(IReadOnlyDictionary<string, string> member, string field, out string value)
    {
        if (member.TryGetValue(field, out value!))
            return true;

        foreach (var (key, candidate) in member)
        {
            if (!string.Equals(key, field, StringComparison.OrdinalIgnoreCase))
                continue;
            value = candidate;
            return true;
        }

        value = "";
        return false;
    }

    public static bool DictionaryEquals(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right)
        => left.Count == right.Count &&
           left.All(kvp => right.TryGetValue(kvp.Key, out var value) && string.Equals(kvp.Value, value, StringComparison.Ordinal));

    public static bool IsNonCanonicalField(string field, string sourceField)
        => string.Equals(field, "id", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(field, sourceField, StringComparison.OrdinalIgnoreCase);
}
