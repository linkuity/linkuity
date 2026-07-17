using Linkuity.Core.Models;

namespace Linkuity.Core.Tests.Models;

public class MdmModelTests
{
    [Fact]
    public void Models_CarryStableIdsProvenanceAndGoldenHistory()
    {
        var projectId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var leftRecordId = Guid.NewGuid();
        var rightRecordId = Guid.NewGuid();
        var clusterId = Guid.NewGuid();
        var goldenRecordId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var project = new Project
        {
            Id = projectId,
            Name = "Customer MDM",
            ContentType = "person",
            MergeConfiguration = new MergeConfiguration
            {
                MergeFields =
                [
                    new MergeField { FieldName = "email", SourcePriority = ["CRM", "Marketing"] }
                ]
            },
            CreatedAt = now
        };
        var source = new Source
        {
            Id = sourceId,
            ProjectId = projectId,
            Name = "CRM",
            CreatedAt = now
        };
        var batch = new IngestBatch
        {
            Id = batchId,
            ProjectId = projectId,
            SourceId = sourceId,
            JobId = Guid.NewGuid(),
            RecordCount = 2,
            CreatedAt = now
        };
        var record = new EntityRecord
        {
            Id = leftRecordId,
            ProjectId = projectId,
            SourceId = sourceId,
            IngestBatchId = batchId,
            SourceRecordId = "crm-001",
            Fields = new Dictionary<string, string> { ["email"] = "alice@example.com" },
            BlockingKeys = ["email:alice@example.com"],
            CreatedAt = now
        };
        var edge = new MatchEdge
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            IngestBatchId = batchId,
            LeftEntityRecordId = leftRecordId,
            RightEntityRecordId = rightRecordId,
            Score = 0.98,
            Method = "batch",
            CreatedAt = now
        };
        var cluster = new Cluster
        {
            Id = clusterId,
            ProjectId = projectId,
            MemberEntityRecordIds = [leftRecordId, rightRecordId],
            CreatedAt = now
        };
        var golden = new GoldenRecord
        {
            Id = goldenRecordId,
            ProjectId = projectId,
            ClusterId = clusterId,
            CurrentVersionId = versionId,
            Fields = new Dictionary<string, string> { ["email"] = "alice@example.com" },
            UpdatedAt = now
        };
        var version = new GoldenRecordVersion
        {
            Id = versionId,
            GoldenRecordId = goldenRecordId,
            ProjectId = projectId,
            ClusterId = clusterId,
            IngestBatchId = batchId,
            VersionNumber = 1,
            Fields = golden.Fields,
            CreatedAt = now
        };
        var reviewTask = new ReviewTask
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            IngestBatchId = batchId,
            NewEntityRecordId = leftRecordId,
            CandidateEntityRecordId = rightRecordId,
            Score = 0.82,
            Reason = "review_threshold",
            Status = "open",
            CreatedAt = now
        };

        Assert.Equal(projectId, source.ProjectId);
        Assert.Equal("email", project.MergeConfiguration!.MergeFields[0].FieldName);
        Assert.Equal(["CRM", "Marketing"], project.MergeConfiguration.MergeFields[0].SourcePriority);
        Assert.Equal(sourceId, batch.SourceId);
        Assert.Equal(batchId, record.IngestBatchId);
        Assert.Contains("email:alice@example.com", record.BlockingKeys);
        Assert.Equal(leftRecordId, edge.LeftEntityRecordId);
        Assert.Contains(rightRecordId, cluster.MemberEntityRecordIds);
        Assert.Equal(versionId, golden.CurrentVersionId);
        Assert.Equal(batchId, version.IngestBatchId);
        Assert.Equal("open", reviewTask.Status);
    }
}
