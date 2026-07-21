using Linkuity.Core.Models;
using Linkuity.Pipeline;

namespace Linkuity.Pipeline.Tests;

public class GoldenRecordServiceTests
{
    private readonly GoldenRecordService _svc = new();

    private static IReadOnlyDictionary<string, string> Rec(params (string Key, string Value)[] fields) =>
        fields.ToDictionary(f => f.Key, f => f.Value);

    [Fact]
    public void Merge_SourcePriority_PicksHighestPrioritySource()
    {
        var clusters = new List<IReadOnlyList<string>> { new[] { "1", "2" } };
        var records = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["1"] = Rec(("source", "CRM"), ("email", "crm@example.com")),
            ["2"] = Rec(("source", "Marketing"), ("email", "mkt@example.com")),
        };
        var config = new MergeConfiguration
        {
            MergeFields = [new MergeField { FieldName = "email", SourcePriority = ["CRM", "Marketing"] }]
        };

        var result = _svc.Merge(clusters, records, config, "source");

        Assert.Equal("crm@example.com", result[0].Fields["email"]);
    }

    [Fact]
    public void Merge_SourcePriority_SkipsEmptyFallsToNextSource()
    {
        var clusters = new List<IReadOnlyList<string>> { new[] { "1", "2" } };
        var records = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["1"] = Rec(("source", "CRM"), ("email", "")),
            ["2"] = Rec(("source", "Marketing"), ("email", "mkt@example.com")),
        };
        var config = new MergeConfiguration
        {
            MergeFields = [new MergeField { FieldName = "email", SourcePriority = ["CRM", "Marketing"] }]
        };

        var result = _svc.Merge(clusters, records, config, "source");

        Assert.Equal("mkt@example.com", result[0].Fields["email"]);
    }

    [Fact]
    public void Merge_SourcePriority_FallsBackToConsensusWhenNoPrioritySourceHasValue()
    {
        var clusters = new List<IReadOnlyList<string>> { new[] { "1", "2", "3" } };
        var records = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["1"] = Rec(("source", "Other"), ("email", "a@example.com")),
            ["2"] = Rec(("source", "Other"), ("email", "a@example.com")),
            ["3"] = Rec(("source", "Other"), ("email", "b@example.com")),
        };
        var config = new MergeConfiguration
        {
            MergeFields = [new MergeField { FieldName = "email", SourcePriority = ["CRM"] }]
        };

        var result = _svc.Merge(clusters, records, config, "source");

        // No "CRM" records → consensus → "a@example.com" (appears twice)
        Assert.Equal("a@example.com", result[0].Fields["email"]);
    }

    [Fact]
    public void Merge_Consensus_MostFrequentValueWins()
    {
        var clusters = new List<IReadOnlyList<string>> { new[] { "1", "2", "3" } };
        var records = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["1"] = Rec(("name", "Smith")),
            ["2"] = Rec(("name", "Smith")),
            ["3"] = Rec(("name", "Smythe")),
        };

        var result = _svc.Merge(clusters, records, null, null);

        Assert.Equal("Smith", result[0].Fields["name"]);
    }

    [Fact]
    public void Merge_Consensus_TiebreakerLongestValueWins()
    {
        var clusters = new List<IReadOnlyList<string>> { new[] { "1", "2" } };
        var records = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["1"] = Rec(("name", "Jon")),
            ["2"] = Rec(("name", "Jonathan")),
        };

        var result = _svc.Merge(clusters, records, null, null);

        Assert.Equal("Jonathan", result[0].Fields["name"]);
    }

    [Fact]
    public void Merge_SingletonCluster_GoldenRecordEqualsSourceRecord()
    {
        var clusters = new List<IReadOnlyList<string>> { new[] { "1" } };
        var records = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["1"] = Rec(("name", "Alice"), ("email", "alice@example.com")),
        };

        var result = _svc.Merge(clusters, records, null, null);

        Assert.Single(result);
        Assert.Equal("Alice", result[0].Fields["name"]);
        Assert.Equal("alice@example.com", result[0].Fields["email"]);
    }

    [Fact]
    public void Merge_FieldNotInConfig_UsesConsensus()
    {
        var clusters = new List<IReadOnlyList<string>> { new[] { "1", "2" } };
        var records = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["1"] = Rec(("source", "CRM"), ("name", "Alice"), ("city", "Boston")),
            ["2"] = Rec(("source", "Marketing"), ("name", "Alice"), ("city", "Boston")),
        };
        var config = new MergeConfiguration
        {
            MergeFields = [new MergeField { FieldName = "name", SourcePriority = ["CRM"] }]
        };

        var result = _svc.Merge(clusters, records, config, "source");

        // "city" not in config → consensus → "Boston"
        Assert.Equal("Boston", result[0].Fields["city"]);
    }

    [Fact]
    public void Merge_SourceFieldExcludedFromOutput()
    {
        var clusters = new List<IReadOnlyList<string>> { new[] { "1" } };
        var records = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["1"] = Rec(("source", "CRM"), ("name", "Alice")),
        };

        var result = _svc.Merge(clusters, records, null, "source");

        Assert.False(result[0].Fields.ContainsKey("source"));
        Assert.True(result[0].Fields.ContainsKey("name"));
    }

    [Fact]
    public void Merge_NullSourceField_WithConfig_UsesConsensus()
    {
        var clusters = new List<IReadOnlyList<string>> { new[] { "1", "2", "3" } };
        var records = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["1"] = Rec(("email", "a@example.com")),
            ["2"] = Rec(("email", "a@example.com")),
            ["3"] = Rec(("email", "b@example.com")),
        };
        var config = new MergeConfiguration
        {
            MergeFields = [new MergeField { FieldName = "email", SourcePriority = ["CRM"] }]
        };

        // sourceField is null → source-priority unavailable → falls back to consensus
        var result = _svc.Merge(clusters, records, config, null);

        Assert.Equal("a@example.com", result[0].Fields["email"]);
    }
}
