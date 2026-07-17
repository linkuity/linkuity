using System.Text.Json;
using Linkuity.Core.Models;

namespace Linkuity.Core.Tests;

public class MatchScoreFactorSerializationTests
{
    [Fact]
    public void MatchEdge_DefaultsDecisionAndBreakdown_WhenNotSet()
    {
        var edge = new MatchEdge
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            IngestBatchId = Guid.NewGuid(),
            LeftEntityRecordId = Guid.NewGuid(),
            RightEntityRecordId = Guid.NewGuid(),
            Score = 0.98,
            Method = "incremental",
            CreatedAt = DateTimeOffset.UtcNow
        };

        Assert.Equal("", edge.Decision);
        Assert.Empty(edge.Breakdown);
    }

    [Fact]
    public void MatchEdge_RoundTripsBreakdownThroughJson()
    {
        var edge = new MatchEdge
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            IngestBatchId = Guid.NewGuid(),
            LeftEntityRecordId = Guid.NewGuid(),
            RightEntityRecordId = Guid.NewGuid(),
            Score = 0.98,
            Method = "incremental",
            Decision = "auto",
            Breakdown = [new MatchScoreFactor("email", 1.0, 0.4, 0.4)],
            CreatedAt = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(edge);
        var restored = JsonSerializer.Deserialize<MatchEdge>(json)!;

        Assert.Equal("auto", restored.Decision);
        var factor = Assert.Single(restored.Breakdown);
        Assert.Equal("email", factor.Signal);
        Assert.Equal(0.4, factor.Contribution, 10);
    }

    [Fact]
    public void MatchEdge_DeserializesLegacyJson_WithoutDecisionOrBreakdown()
    {
        // A pre-Milestone-17 edge had no "Decision" / "Breakdown" members.
        var legacy = """
        {
          "Id": "11111111-1111-1111-1111-111111111111",
          "ProjectId": "22222222-2222-2222-2222-222222222222",
          "IngestBatchId": "33333333-3333-3333-3333-333333333333",
          "LeftEntityRecordId": "44444444-4444-4444-4444-444444444444",
          "RightEntityRecordId": "55555555-5555-5555-5555-555555555555",
          "Score": 0.98,
          "Method": "incremental",
          "CreatedAt": "2026-06-01T00:00:00+00:00"
        }
        """;

        var edge = JsonSerializer.Deserialize<MatchEdge>(legacy)!;

        Assert.Equal("incremental", edge.Method);
        Assert.Equal("", edge.Decision);
        Assert.Empty(edge.Breakdown);
    }

    [Fact]
    public void ReviewTask_RoundTripsBreakdownThroughJson()
    {
        var task = new ReviewTask
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            IngestBatchId = Guid.NewGuid(),
            NewEntityRecordId = Guid.NewGuid(),
            CandidateEntityRecordId = Guid.NewGuid(),
            Score = 0.8,
            Reason = "review_threshold",
            Status = "open",
            Breakdown = [new MatchScoreFactor("last_name", 1.0, 2.0, 0.4)],
            CreatedAt = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(task);
        var restored = JsonSerializer.Deserialize<ReviewTask>(json)!;

        var factor = Assert.Single(restored.Breakdown);
        Assert.Equal("last_name", factor.Signal);
        Assert.Equal(0.4, factor.Contribution, 10);
    }

    [Fact]
    public void ReviewTask_DeserializesLegacyJson_WithoutBreakdown()
    {
        var legacy = """
        {
          "Id": "11111111-1111-1111-1111-111111111111",
          "ProjectId": "22222222-2222-2222-2222-222222222222",
          "IngestBatchId": "33333333-3333-3333-3333-333333333333",
          "NewEntityRecordId": "44444444-4444-4444-4444-444444444444",
          "CandidateEntityRecordId": "55555555-5555-5555-5555-555555555555",
          "Score": 0.8,
          "Reason": "review_threshold",
          "Status": "open",
          "CreatedAt": "2026-06-01T00:00:00+00:00"
        }
        """;

        var task = JsonSerializer.Deserialize<ReviewTask>(legacy)!;

        Assert.Equal("review_threshold", task.Reason);
        Assert.Empty(task.Breakdown);
    }
}
