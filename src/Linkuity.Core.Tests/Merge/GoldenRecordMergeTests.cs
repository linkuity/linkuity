using Linkuity.Core.Merge;

namespace Linkuity.Core.Tests.Merge;

public class GoldenRecordMergeTests
{
    private static IReadOnlyDictionary<string, string> Rec(params (string Key, string Value)[] fields) =>
        fields.ToDictionary(f => f.Key, f => f.Value);

    private static readonly IReadOnlyDictionary<string, string[]> NoPriority =
        new Dictionary<string, string[]>();

    // --- MergeByConsensus ---

    [Fact]
    public void MergeByConsensus_MajorityValueWins()
    {
        var members = new List<IReadOnlyDictionary<string, string>>
        {
            Rec(("source", "a"), ("city", "London")),
            Rec(("source", "b"), ("city", "London")),
            Rec(("source", "c"), ("city", "Paris")),
        };

        var result = GoldenRecordMerge.MergeByConsensus(members, "city");

        Assert.Equal("London", result);
    }

    [Fact]
    public void MergeByConsensus_TieBrokenByLongestValue()
    {
        var members = new List<IReadOnlyDictionary<string, string>>
        {
            Rec(("source", "a"), ("name", "Robert Johnson")),
            Rec(("source", "b"), ("name", "Bob")),
        };

        var result = GoldenRecordMerge.MergeByConsensus(members, "name");

        Assert.Equal("Robert Johnson", result);
    }

    [Fact]
    public void MergeByConsensus_ReturnsEmptyWhenFieldAbsentFromAllMembers()
    {
        var members = new List<IReadOnlyDictionary<string, string>> { Rec(("source", "a")) };

        var result = GoldenRecordMerge.MergeByConsensus(members, "city");

        Assert.Equal("", result);
    }

    [Fact]
    public void MergeByConsensus_TreatsWhitespaceOnlyValueAsMissing()
    {
        var members = new List<IReadOnlyDictionary<string, string>>
        {
            Rec(("city", "   ")),
            Rec(("city", "Paris")),
        };

        var result = GoldenRecordMerge.MergeByConsensus(members, "city");

        Assert.Equal("Paris", result);
    }

    [Fact]
    public void MergeByConsensus_GroupsValuesCaseInsensitively()
    {
        var members = new List<IReadOnlyDictionary<string, string>>
        {
            Rec(("name", "Alice")),
            Rec(("name", "ALICE")),
            Rec(("name", "Bob")),
        };

        var result = GoldenRecordMerge.MergeByConsensus(members, "name");

        // "Alice"/"ALICE" count as the same value (2) and outvote "Bob" (1).
        Assert.Equal("Alice", result, ignoreCase: true);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void MergeByConsensus_FullTie_OrderIndependent(int i0, int i1)
    {
        // Two distinct values, each appearing once (tied count), same length (tied length) ->
        // the only thing left to break the tie on is content, not position in the members list.
        var members = new List<IReadOnlyDictionary<string, string>>
        {
            Rec(("city", "Oslo")),
            Rec(("city", "Bern")),
        };
        var ordered = new[] { members[0], members[1] };
        var input = new List<IReadOnlyDictionary<string, string>> { ordered[i0], ordered[i1] };

        var result = GoldenRecordMerge.MergeByConsensus(input, "city");

        Assert.Equal("Bern", result); // alphabetically first, regardless of arrival order
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void MergeByConsensus_CasingTie_ReturnsSameCasingRegardlessOfArrivalOrder(int i0, int i1)
    {
        // "Alice" and "ALICE" are the same value under the case-insensitive grouping (tied count,
        // tied length), so the ONLY thing left to pick a winning casing is content, never which
        // record's casing happened to become the group's Key first.
        var members = new List<IReadOnlyDictionary<string, string>>
        {
            Rec(("name", "Alice")),
            Rec(("name", "ALICE")),
        };
        var input = new List<IReadOnlyDictionary<string, string>> { members[i0], members[i1] };

        var result = GoldenRecordMerge.MergeByConsensus(input, "name");

        // Not just case-insensitively equal — the exact same casing regardless of order.
        Assert.Equal("ALICE", result);
    }

    // --- MergeByPriority ---

    [Fact]
    public void MergeByPriority_HigherPrioritySourceWins()
    {
        var members = new List<IReadOnlyDictionary<string, string>>
        {
            Rec(("source", "crm"), ("name", "Alice CRM")),
            Rec(("source", "web"), ("name", "Alice Web")),
        };

        var result = GoldenRecordMerge.MergeByPriority(members, "name", "source", ["crm", "web"]);

        Assert.Equal("Alice CRM", result);
    }

    [Fact]
    public void MergeByPriority_FallsThroughWhenTopPriorityLacksValue()
    {
        var members = new List<IReadOnlyDictionary<string, string>>
        {
            Rec(("source", "crm")), // no "name"
            Rec(("source", "web"), ("name", "Alice Web")),
        };

        var result = GoldenRecordMerge.MergeByPriority(members, "name", "source", ["crm", "web"]);

        Assert.Equal("Alice Web", result);
    }

    [Fact]
    public void MergeByPriority_FallsToConsensusWhenNoPrioritySourcePresent()
    {
        var members = new List<IReadOnlyDictionary<string, string>>
        {
            Rec(("source", "web"), ("name", "Alice")),
            Rec(("source", "web"), ("name", "Alice")),
            Rec(("source", "web"), ("name", "Bob")),
        };

        var result = GoldenRecordMerge.MergeByPriority(members, "name", "source", ["crm", "hr"]);

        Assert.Equal("Alice", result);
    }

    [Theory]
    [InlineData(0, 1, 2)]
    [InlineData(2, 1, 0)]
    [InlineData(1, 2, 0)]
    public void MergeByPriority_MultipleMembersFromTopSourceDisagree_OrderIndependent(int i0, int i1, int i2)
    {
        // Three members share the top-priority source "crm" but disagree on "name": two say
        // "Alice", one says "Zoe". The disagreement must resolve by majority, not by which
        // record happens to come first in the list.
        var candidates = new[]
        {
            Rec(("source", "crm"), ("name", "Alice")),
            Rec(("source", "crm"), ("name", "Zoe")),
            Rec(("source", "crm"), ("name", "Alice")),
        };
        var members = new List<IReadOnlyDictionary<string, string>>
            { candidates[i0], candidates[i1], candidates[i2] };

        var result = GoldenRecordMerge.MergeByPriority(members, "name", "source", ["crm", "web"]);

        Assert.Equal("Alice", result);
    }

    // --- MergeFields ---

    [Fact]
    public void MergeFields_ExcludesIdAndSourceField()
    {
        var members = new List<IReadOnlyDictionary<string, string>>
        {
            Rec(("id", "123"), ("source", "crm"), ("name", "Alice")),
        };

        var result = GoldenRecordMerge.MergeFields(members, NoPriority, "source");

        Assert.False(result.ContainsKey("id"));
        Assert.False(result.ContainsKey("source"));
        Assert.True(result.ContainsKey("name"));
    }

    [Fact]
    public void MergeFields_AppliesPriorityForConfiguredFieldsAndConsensusForRest()
    {
        var members = new List<IReadOnlyDictionary<string, string>>
        {
            Rec(("source", "crm"), ("name", "Alice CRM"), ("city", "London")),
            Rec(("source", "web"), ("name", "Alice Web"), ("city", "London")),
            Rec(("source", "web"), ("name", "Alice Web"), ("city", "Paris")),
        };
        var priority = new Dictionary<string, string[]> { ["name"] = ["crm", "web"] };

        var result = GoldenRecordMerge.MergeFields(members, priority, "source");

        Assert.Equal("Alice CRM", result["name"]);
        Assert.Equal("London", result["city"]);
    }

    [Fact]
    public void MergeFields_FieldUniverseIsScopedToGivenMembers()
    {
        // A field present on some other cluster's members must not leak into this cluster's
        // golden record just because it happens to exist in the overall corpus.
        var members = new List<IReadOnlyDictionary<string, string>>
        {
            Rec(("name", "Alice")),
        };

        var result = GoldenRecordMerge.MergeFields(members, NoPriority, "source");

        Assert.False(result.ContainsKey("phone"));
    }

    [Theory]
    [InlineData(0, 1, 2)]
    [InlineData(2, 1, 0)]
    [InlineData(1, 2, 0)]
    public void MergeFields_IsOrderIndependent(int i0, int i1, int i2)
    {
        var candidates = new[]
        {
            Rec(("source", "crm"), ("name", "Alice"), ("city", "Bern")),
            Rec(("source", "crm"), ("name", "Zoe"), ("city", "Bern")),
            Rec(("source", "crm"), ("name", "Alice"), ("city", "Oslo")),
        };
        var members = new List<IReadOnlyDictionary<string, string>>
            { candidates[i0], candidates[i1], candidates[i2] };
        var priority = new Dictionary<string, string[]> { ["name"] = ["crm"] };

        var result = GoldenRecordMerge.MergeFields(members, priority, "source");

        Assert.Equal("Alice", result["name"]);  // priority-tier majority
        Assert.Equal("Bern", result["city"]);   // consensus majority
    }

    // --- DictionaryEquals ---

    [Fact]
    public void DictionaryEquals_TrueForSameContentDifferentOrder()
    {
        var left = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" };
        var right = new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" };

        Assert.True(GoldenRecordMerge.DictionaryEquals(left, right));
    }

    [Fact]
    public void DictionaryEquals_FalseForDifferentValue()
    {
        var left = new Dictionary<string, string> { ["a"] = "1" };
        var right = new Dictionary<string, string> { ["a"] = "2" };

        Assert.False(GoldenRecordMerge.DictionaryEquals(left, right));
    }

    [Fact]
    public void DictionaryEquals_FalseForDifferentKeys()
    {
        var left = new Dictionary<string, string> { ["a"] = "1" };
        var right = new Dictionary<string, string> { ["b"] = "1" };

        Assert.False(GoldenRecordMerge.DictionaryEquals(left, right));
    }

    [Fact]
    public void DictionaryEquals_FalseForDifferentCount()
    {
        var left = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" };
        var right = new Dictionary<string, string> { ["a"] = "1" };

        Assert.False(GoldenRecordMerge.DictionaryEquals(left, right));
    }
}
