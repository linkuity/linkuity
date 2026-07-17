using Linkuity.Infrastructure.Local;

namespace Linkuity.Infrastructure.Local.Tests;

public class DurableBackwardCompatibilityTests
{
    [Fact]
    public async Task PreMilestone17Database_LoadsWithBreakdownDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), "linkuity-bc-" + Guid.NewGuid().ToString("N") + ".json");
        var projectId = Guid.NewGuid();
        var legacyJson = $$"""
        {
          "Projects": [
            { "Id": "{{projectId}}", "Name": "Legacy", "ContentType": "person", "MergeConfiguration": null, "CreatedAt": "2026-06-01T00:00:00+00:00" }
          ],
          "Sources": [],
          "IngestBatches": [],
          "EntityRecords": [],
          "MatchEdges": [
            {
              "Id": "11111111-1111-1111-1111-111111111111",
              "ProjectId": "{{projectId}}",
              "IngestBatchId": "33333333-3333-3333-3333-333333333333",
              "LeftEntityRecordId": "44444444-4444-4444-4444-444444444444",
              "RightEntityRecordId": "55555555-5555-5555-5555-555555555555",
              "Score": 0.98,
              "Method": "incremental",
              "CreatedAt": "2026-06-01T00:00:00+00:00"
            }
          ],
          "Clusters": [],
          "GoldenRecords": [],
          "GoldenRecordVersions": [],
          "ReviewTasks": [
            {
              "Id": "66666666-6666-6666-6666-666666666666",
              "ProjectId": "{{projectId}}",
              "IngestBatchId": "33333333-3333-3333-3333-333333333333",
              "NewEntityRecordId": "44444444-4444-4444-4444-444444444444",
              "CandidateEntityRecordId": "55555555-5555-5555-5555-555555555555",
              "Score": 0.80,
              "Reason": "review_threshold",
              "Status": "open",
              "CreatedAt": "2026-06-01T00:00:00+00:00"
            }
          ]
        }
        """;
        await File.WriteAllTextAsync(path, legacyJson);

        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = path });

        var edges = await store.ListMatchEdgesAsync(projectId, CancellationToken.None);
        var edge = Assert.Single(edges);
        Assert.Equal("", edge.Decision);
        Assert.Empty(edge.Breakdown);

        var tasks = await store.ListReviewTasksAsync(projectId, CancellationToken.None);
        var task = Assert.Single(tasks);
        Assert.Empty(task.Breakdown);
    }

    [Fact]
    public async Task PreM22Database_LoadsWithLineageDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), "linkuity-bc-m22-" + Guid.NewGuid().ToString("N") + ".json");
        var projectId = Guid.NewGuid();
        var clusterId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        var json = $$"""
        {
          "Projects": [{ "Id": "{{projectId}}", "Name": "MDM", "ContentType": "person", "CreatedAt": "2026-01-01T00:00:00+00:00" }],
          "Sources": [], "IngestBatches": [], "EntityRecords": [], "MatchEdges": [],
          "Clusters": [{ "Id": "{{clusterId}}", "ProjectId": "{{projectId}}", "MemberEntityRecordIds": ["{{recordId}}"], "CreatedAt": "2026-01-01T00:00:00+00:00" }],
          "GoldenRecords": [], "GoldenRecordVersions": [], "ReviewTasks": []
        }
        """;
        await File.WriteAllTextAsync(path, json);
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = path });

        var clusters = await store.ListClustersAsync(projectId, CancellationToken.None);
        Assert.Equal("active", Assert.Single(clusters).Status);
        Assert.Null(Assert.Single(clusters).MergedIntoClusterId);
        Assert.Empty(await store.ListClusterMergeEventsAsync(projectId, CancellationToken.None));
    }
}
