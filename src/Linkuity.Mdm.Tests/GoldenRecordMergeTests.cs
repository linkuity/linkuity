using Linkuity.Core.Models;
using Linkuity.Mdm.Resolution;

namespace Linkuity.Mdm.Tests;

public class GoldenRecordMergeTests
{
    private static EntityRecord MakeRecord(Guid projectId, Dictionary<string, string> fields) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        SourceId = Guid.NewGuid(),
        IngestBatchId = Guid.NewGuid(),
        SourceRecordId = Guid.NewGuid().ToString(),
        Fields = fields,
        CreatedAt = DateTimeOffset.UtcNow
    };

    // --- MergeByPriority ---

    [Fact]
    public void MergeByPriority_HigherPrioritySourceWins()
    {
        var pid = Guid.NewGuid();
        var members = new List<EntityRecord>
        {
            MakeRecord(pid, new() { ["source"] = "crm", ["name"] = "Alice CRM" }),
            MakeRecord(pid, new() { ["source"] = "web", ["name"] = "Alice Web" }),
        };

        var result = GoldenRecordMerge.MergeByPriority(members, "name", ["crm", "web"]);

        Assert.Equal("Alice CRM", result);
    }

    [Fact]
    public void MergeByPriority_FallsThroughWhenTopPriorityLacksValue()
    {
        var pid = Guid.NewGuid();
        var members = new List<EntityRecord>
        {
            MakeRecord(pid, new() { ["source"] = "crm" }), // no "name"
            MakeRecord(pid, new() { ["source"] = "web", ["name"] = "Alice Web" }),
        };

        var result = GoldenRecordMerge.MergeByPriority(members, "name", ["crm", "web"]);

        Assert.Equal("Alice Web", result);
    }

    [Fact]
    public void MergeByPriority_FallsToConsensusWhenNoPrioritySourcePresent()
    {
        var pid = Guid.NewGuid();
        // Priority sources "crm" and "hr" are absent; falls back to consensus
        var members = new List<EntityRecord>
        {
            MakeRecord(pid, new() { ["source"] = "web", ["name"] = "Alice" }),
            MakeRecord(pid, new() { ["source"] = "web", ["name"] = "Alice" }),
            MakeRecord(pid, new() { ["source"] = "web", ["name"] = "Bob" }),
        };

        var result = GoldenRecordMerge.MergeByPriority(members, "name", ["crm", "hr"]);

        Assert.Equal("Alice", result); // majority consensus
    }

    // --- MergeByConsensus ---

    [Fact]
    public void MergeByConsensus_MajorityValueWins()
    {
        var pid = Guid.NewGuid();
        var members = new List<EntityRecord>
        {
            MakeRecord(pid, new() { ["source"] = "a", ["city"] = "London" }),
            MakeRecord(pid, new() { ["source"] = "b", ["city"] = "London" }),
            MakeRecord(pid, new() { ["source"] = "c", ["city"] = "Paris" }),
        };

        var result = GoldenRecordMerge.MergeByConsensus(members, "city");

        Assert.Equal("London", result);
    }

    [Fact]
    public void MergeByConsensus_TieBrokenByLongestValue()
    {
        var pid = Guid.NewGuid();
        var members = new List<EntityRecord>
        {
            MakeRecord(pid, new() { ["source"] = "a", ["name"] = "Robert Johnson" }),
            MakeRecord(pid, new() { ["source"] = "b", ["name"] = "Bob" }),
        };

        var result = GoldenRecordMerge.MergeByConsensus(members, "name");

        Assert.Equal("Robert Johnson", result); // tie (1 each); longest wins
    }

    [Fact]
    public void MergeByConsensus_ReturnsEmptyWhenFieldAbsentFromAllMembers()
    {
        var pid = Guid.NewGuid();
        var members = new List<EntityRecord>
        {
            MakeRecord(pid, new() { ["source"] = "a" }),
        };

        var result = GoldenRecordMerge.MergeByConsensus(members, "city");

        Assert.Equal("", result);
    }

    // --- MergeFields(Project, members) ---

    [Fact]
    public void MergeFields_ExcludesNonCanonicalFields_IdAndSource()
    {
        var pid = Guid.NewGuid();
        var project = new Project
        {
            Id = pid,
            Name = "test",
            ContentType = "person",
            CreatedAt = DateTimeOffset.MinValue
        };
        var members = new List<EntityRecord>
        {
            MakeRecord(pid, new() { ["id"] = "123", ["source"] = "crm", ["name"] = "Alice" }),
        };

        var result = GoldenRecordMerge.MergeFields(project, members);

        Assert.False(result.ContainsKey("id"));
        Assert.False(result.ContainsKey("source"));
        Assert.True(result.ContainsKey("name"));
    }

    [Fact]
    public void MergeFields_AppliesPriorityForConfiguredFieldsAndConsensusForRest()
    {
        var pid = Guid.NewGuid();
        var project = new Project
        {
            Id = pid,
            Name = "test",
            ContentType = "person",
            MergeConfiguration = new MergeConfiguration
            {
                MergeFields =
                [
                    new MergeField { FieldName = "name", SourcePriority = ["crm", "web"] }
                ]
            },
            CreatedAt = DateTimeOffset.MinValue
        };
        var members = new List<EntityRecord>
        {
            MakeRecord(pid, new() { ["source"] = "crm", ["name"] = "Alice CRM", ["city"] = "London" }),
            MakeRecord(pid, new() { ["source"] = "web", ["name"] = "Alice Web", ["city"] = "London" }),
            MakeRecord(pid, new() { ["source"] = "web", ["name"] = "Alice Web", ["city"] = "Paris" }),
        };

        var result = GoldenRecordMerge.MergeFields(project, members);

        Assert.Equal("Alice CRM", result["name"]); // priority: crm wins
        Assert.Equal("London", result["city"]);    // consensus: London 2 vs 1
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
