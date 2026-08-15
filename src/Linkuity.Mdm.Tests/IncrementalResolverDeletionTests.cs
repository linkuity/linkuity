using Linkuity.Core.Models;
using Linkuity.Mdm.Resolution;

namespace Linkuity.Mdm.Tests;

public class IncrementalResolverDeletionTests
{
    private static readonly IncrementalResolver Resolver =
        new(Linkuity.Matching.MatchingDefaults.CreateEngine(), hasIndex: false,
            new Linkuity.Matching.Clustering.CohesionClusterMergePolicy());

    private static Project MakeProject(Guid id, MergeConfiguration? merge = null) => new()
    {
        Id = id,
        Name = "test",
        ContentType = "person",
        MergeConfiguration = merge,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static EntityRecord MakeRecord(
        Guid projectId, string sourceRecordId, IReadOnlyDictionary<string, string> fields, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        ProjectId = projectId,
        SourceId = Guid.NewGuid(),
        IngestBatchId = Guid.NewGuid(),
        SourceRecordId = sourceRecordId,
        Fields = fields,
        CreatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public void SingletonRecord_NoCluster_MarksDeletedNoClusterMutation()
    {
        var projectId = Guid.NewGuid();
        var project = MakeProject(projectId);
        var context = new InMemoryResolutionContext();
        var existingId = Guid.NewGuid();
        var existing = MakeRecord(projectId, "s-1", new Dictionary<string, string> { ["email"] = "a@example.com" }, existingId);
        context.Records.Add(existing);
        var ingestBatchId = Guid.NewGuid();

        var (deletedIds, mutations) = Resolver.ClassifyAndDetachDeletions(
            project, Linkuity.Matching.MatchingDefaults.CreatePersonProfile(), ["s-1"], ingestBatchId, context, DateTimeOffset.UtcNow);

        Assert.Equal([existingId], deletedIds);
        var updated = Assert.Single(mutations.RecordsToUpdate);
        Assert.Equal(existingId, updated.Id);
        Assert.NotNull(updated.DeletedAt);

        var evt = Assert.Single(mutations.DeletionEventsToInsert);
        Assert.Equal(existingId, evt.DeletedEntityRecordId);
        Assert.Null(evt.PreviousClusterId);
        Assert.Empty(mutations.ClustersToUpsert);
    }

    [Fact]
    public void MultiMemberCluster_SurvivorsKeepClusterIdentity_GoldenRecomputed()
    {
        var projectId = Guid.NewGuid();
        var merge = new MergeConfiguration { MergeFields = [] };
        var project = MakeProject(projectId, merge);
        var context = new InMemoryResolutionContext();

        var deletedOldId = Guid.NewGuid();
        var sibling1Id = Guid.NewGuid();
        var sibling2Id = Guid.NewGuid();
        var deletedOld = MakeRecord(projectId, "d-1", new Dictionary<string, string> { ["name"] = "Alice", ["city"] = "Bern" }, deletedOldId);
        var sibling1 = MakeRecord(projectId, "sib-1", new Dictionary<string, string> { ["name"] = "Alice", ["city"] = "Bern" }, sibling1Id);
        var sibling2 = MakeRecord(projectId, "sib-2", new Dictionary<string, string> { ["name"] = "Alice", ["city"] = "Bern" }, sibling2Id);
        context.Records.AddRange([deletedOld, sibling1, sibling2]);

        var clusterId = Guid.NewGuid();
        context.Clusters.Add(new Cluster
        {
            Id = clusterId, ProjectId = projectId,
            MemberEntityRecordIds = [deletedOldId, sibling1Id, sibling2Id],
            CreatedAt = DateTimeOffset.UtcNow
        });
        var goldenId = Guid.NewGuid();
        context.GoldenRecords.Add(new GoldenRecord
        {
            Id = goldenId, ProjectId = projectId, ClusterId = clusterId, CurrentVersionId = Guid.NewGuid(),
            Fields = new Dictionary<string, string> { ["name"] = "Alice", ["city"] = "Bern" }, UpdatedAt = DateTimeOffset.UtcNow
        });

        var ingestBatchId = Guid.NewGuid();
        var (deletedIds, mutations) = Resolver.ClassifyAndDetachDeletions(
            project, Linkuity.Matching.MatchingDefaults.CreatePersonProfile(), ["d-1"], ingestBatchId, context, DateTimeOffset.UtcNow);

        Assert.Equal([deletedOldId], deletedIds);

        var reducedCluster = Assert.Single(mutations.ClustersToUpsert);
        Assert.Equal(clusterId, reducedCluster.Id); // SAME cluster id — F28 identity retention
        Assert.Equal([sibling1Id, sibling2Id], reducedCluster.MemberEntityRecordIds);

        var recomputedGolden = Assert.Single(mutations.GoldenRecordsToUpsert);
        Assert.Equal(goldenId, recomputedGolden.Id);
        Assert.Equal("Alice", recomputedGolden.Fields["name"]);

        var evt = Assert.Single(mutations.DeletionEventsToInsert);
        Assert.Equal(clusterId, evt.PreviousClusterId);
    }

    [Fact]
    public void TwoMemberCluster_SurvivorKeepsOriginalClusterId_StatusRemainsActive()
    {
        var projectId = Guid.NewGuid();
        var project = MakeProject(projectId);
        var context = new InMemoryResolutionContext();

        var deletedOldId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var deletedOld = MakeRecord(projectId, "d-1", new Dictionary<string, string> { ["name"] = "Alice" }, deletedOldId);
        var survivor = MakeRecord(projectId, "sib-1", new Dictionary<string, string> { ["name"] = "Alice" }, survivorId);
        context.Records.AddRange([deletedOld, survivor]);

        var clusterId = Guid.NewGuid();
        context.Clusters.Add(new Cluster
        {
            Id = clusterId, ProjectId = projectId, MemberEntityRecordIds = [deletedOldId, survivorId],
            CreatedAt = DateTimeOffset.UtcNow
        });

        var (_, mutations) = Resolver.ClassifyAndDetachDeletions(
            project, Linkuity.Matching.MatchingDefaults.CreatePersonProfile(), ["d-1"], Guid.NewGuid(), context, DateTimeOffset.UtcNow);

        var reducedCluster = Assert.Single(mutations.ClustersToUpsert);
        Assert.Equal(clusterId, reducedCluster.Id);
        Assert.Equal([survivorId], reducedCluster.MemberEntityRecordIds);
        Assert.Equal("active", reducedCluster.Status);
    }

    [Fact]
    public void ClusterHadOnlyThisMember_ClusterTombstonedGoldenCleared()
    {
        var projectId = Guid.NewGuid();
        var project = MakeProject(projectId);
        var context = new InMemoryResolutionContext();

        var oldId = Guid.NewGuid();
        var old = MakeRecord(projectId, "d-1", new Dictionary<string, string> { ["name"] = "Alice" }, oldId);
        context.Records.Add(old);

        var clusterId = Guid.NewGuid();
        context.Clusters.Add(new Cluster
        {
            Id = clusterId, ProjectId = projectId, MemberEntityRecordIds = [oldId], CreatedAt = DateTimeOffset.UtcNow
        });
        var goldenId = Guid.NewGuid();
        context.GoldenRecords.Add(new GoldenRecord
        {
            Id = goldenId, ProjectId = projectId, ClusterId = clusterId, CurrentVersionId = Guid.NewGuid(),
            Fields = new Dictionary<string, string> { ["name"] = "Alice" }, UpdatedAt = DateTimeOffset.UtcNow
        });

        var (_, mutations) = Resolver.ClassifyAndDetachDeletions(
            project, Linkuity.Matching.MatchingDefaults.CreatePersonProfile(), ["d-1"], Guid.NewGuid(), context, DateTimeOffset.UtcNow);

        var tombstoned = Assert.Single(mutations.ClustersToUpsert);
        Assert.Equal(clusterId, tombstoned.Id);
        Assert.Equal("merged", tombstoned.Status);
        Assert.Null(tombstoned.MergedIntoClusterId);
        Assert.Equal(clusterId, Assert.Single(mutations.GoldenRecordClusterIdsToClear));
        Assert.Empty(mutations.GoldenRecordsToUpsert);
    }

    [Fact]
    public void GoldenRecordAlreadyVersioned_NextVersionNumberIsSequential()
    {
        var projectId = Guid.NewGuid();
        var merge = new MergeConfiguration { MergeFields = [] };
        var project = MakeProject(projectId, merge);
        var context = new InMemoryResolutionContext();

        var deletedOldId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var deletedOld = MakeRecord(projectId, "d-1", new Dictionary<string, string> { ["name"] = "Alice" }, deletedOldId);
        var survivor = MakeRecord(projectId, "sib-1", new Dictionary<string, string> { ["name"] = "Alice" }, survivorId);
        context.Records.AddRange([deletedOld, survivor]);

        var clusterId = Guid.NewGuid();
        context.Clusters.Add(new Cluster
        {
            Id = clusterId, ProjectId = projectId, MemberEntityRecordIds = [deletedOldId, survivorId],
            CreatedAt = DateTimeOffset.UtcNow
        });
        var goldenId = Guid.NewGuid();
        context.GoldenRecords.Add(new GoldenRecord
        {
            Id = goldenId, ProjectId = projectId, ClusterId = clusterId, CurrentVersionId = Guid.NewGuid(),
            Fields = new Dictionary<string, string> { ["name"] = "Alice" }, UpdatedAt = DateTimeOffset.UtcNow
        });
        context.GoldenRecordVersions.AddRange(
        [
            new GoldenRecordVersion
            {
                Id = Guid.NewGuid(), GoldenRecordId = goldenId, ProjectId = projectId, ClusterId = clusterId,
                IngestBatchId = Guid.NewGuid(), VersionNumber = 1,
                Fields = new Dictionary<string, string> { ["name"] = "Alice" }, CreatedAt = DateTimeOffset.UtcNow
            },
            new GoldenRecordVersion
            {
                Id = Guid.NewGuid(), GoldenRecordId = goldenId, ProjectId = projectId, ClusterId = clusterId,
                IngestBatchId = Guid.NewGuid(), VersionNumber = 2,
                Fields = new Dictionary<string, string> { ["name"] = "Alice" }, CreatedAt = DateTimeOffset.UtcNow
            }
        ]);

        var (_, mutations) = Resolver.ClassifyAndDetachDeletions(
            project, Linkuity.Matching.MatchingDefaults.CreatePersonProfile(), ["d-1"], Guid.NewGuid(), context, DateTimeOffset.UtcNow);

        var newVersion = Assert.Single(mutations.VersionsToInsert);
        Assert.Equal(3, newVersion.VersionNumber);
    }

    [Fact]
    public void RecomputedVersion_AttributedToSuppliedIngestBatchId_NotDeletedRecordsOwnBatch()
    {
        var projectId = Guid.NewGuid();
        var merge = new MergeConfiguration { MergeFields = [] };
        var project = MakeProject(projectId, merge);
        var context = new InMemoryResolutionContext();

        var deletedOldId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var deletedOld = MakeRecord(projectId, "d-1", new Dictionary<string, string> { ["name"] = "Alice" }, deletedOldId);
        var survivor = MakeRecord(projectId, "sib-1", new Dictionary<string, string> { ["name"] = "Alice" }, survivorId);
        context.Records.AddRange([deletedOld, survivor]);

        var clusterId = Guid.NewGuid();
        context.Clusters.Add(new Cluster
        {
            Id = clusterId, ProjectId = projectId, MemberEntityRecordIds = [deletedOldId, survivorId],
            CreatedAt = DateTimeOffset.UtcNow
        });
        var goldenId = Guid.NewGuid();
        context.GoldenRecords.Add(new GoldenRecord
        {
            Id = goldenId, ProjectId = projectId, ClusterId = clusterId, CurrentVersionId = Guid.NewGuid(),
            Fields = new Dictionary<string, string> { ["name"] = "Alice" }, UpdatedAt = DateTimeOffset.UtcNow
        });

        var deletionIngestBatchId = Guid.NewGuid();
        var (_, mutations) = Resolver.ClassifyAndDetachDeletions(
            project, Linkuity.Matching.MatchingDefaults.CreatePersonProfile(), ["d-1"], deletionIngestBatchId, context, DateTimeOffset.UtcNow);

        var newVersion = Assert.Single(mutations.VersionsToInsert);
        Assert.Equal(deletionIngestBatchId, newVersion.IngestBatchId);
        Assert.NotEqual(deletedOld.IngestBatchId, newVersion.IngestBatchId);
    }

    [Fact]
    public void TwoDeletions_SameBatch_SameCluster_BothDetachesReflectedInSurvivorList()
    {
        var projectId = Guid.NewGuid();
        var project = MakeProject(projectId);
        var context = new InMemoryResolutionContext();

        var deletedOld1Id = Guid.NewGuid();
        var deletedOld2Id = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var deletedOld1 = MakeRecord(projectId, "d-1", new Dictionary<string, string> { ["name"] = "Alice" }, deletedOld1Id);
        var deletedOld2 = MakeRecord(projectId, "d-2", new Dictionary<string, string> { ["name"] = "Bob" }, deletedOld2Id);
        var survivor = MakeRecord(projectId, "sib-1", new Dictionary<string, string> { ["name"] = "Carol" }, survivorId);
        context.Records.AddRange([deletedOld1, deletedOld2, survivor]);

        var clusterId = Guid.NewGuid();
        context.Clusters.Add(new Cluster
        {
            Id = clusterId, ProjectId = projectId,
            MemberEntityRecordIds = [deletedOld1Id, deletedOld2Id, survivorId],
            CreatedAt = DateTimeOffset.UtcNow
        });

        var (deletedIds, mutations) = Resolver.ClassifyAndDetachDeletions(
            project, Linkuity.Matching.MatchingDefaults.CreatePersonProfile(), ["d-1", "d-2"], Guid.NewGuid(), context, DateTimeOffset.UtcNow);

        Assert.Equal(2, deletedIds.Count);

        var finalClusterState = mutations.ClustersToUpsert.Last(c => c.Id == clusterId);
        Assert.Equal([survivorId], finalClusterState.MemberEntityRecordIds);
    }

    [Fact]
    public void TwoDeletions_SameBatch_SameGoldenRecord_VersionNumbersAreSequentialNotDuplicated()
    {
        var projectId = Guid.NewGuid();
        var merge = new MergeConfiguration { MergeFields = [] };
        var project = MakeProject(projectId, merge);
        var context = new InMemoryResolutionContext();

        var deletedOld1Id = Guid.NewGuid();
        var deletedOld2Id = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var deletedOld1 = MakeRecord(projectId, "d-1", new Dictionary<string, string> { ["name"] = "Alice" }, deletedOld1Id);
        var deletedOld2 = MakeRecord(projectId, "d-2", new Dictionary<string, string> { ["name"] = "Bob" }, deletedOld2Id);
        var survivor = MakeRecord(projectId, "sib-1", new Dictionary<string, string> { ["name"] = "Carol" }, survivorId);
        context.Records.AddRange([deletedOld1, deletedOld2, survivor]);

        var clusterId = Guid.NewGuid();
        context.Clusters.Add(new Cluster
        {
            Id = clusterId, ProjectId = projectId,
            MemberEntityRecordIds = [deletedOld1Id, deletedOld2Id, survivorId],
            CreatedAt = DateTimeOffset.UtcNow
        });
        var goldenId = Guid.NewGuid();
        context.GoldenRecords.Add(new GoldenRecord
        {
            Id = goldenId, ProjectId = projectId, ClusterId = clusterId, CurrentVersionId = Guid.NewGuid(),
            Fields = new Dictionary<string, string> { ["name"] = "Alice" }, UpdatedAt = DateTimeOffset.UtcNow
        });
        context.GoldenRecordVersions.AddRange(
        [
            new GoldenRecordVersion
            {
                Id = Guid.NewGuid(), GoldenRecordId = goldenId, ProjectId = projectId, ClusterId = clusterId,
                IngestBatchId = Guid.NewGuid(), VersionNumber = 1,
                Fields = new Dictionary<string, string> { ["name"] = "Alice" }, CreatedAt = DateTimeOffset.UtcNow
            },
            new GoldenRecordVersion
            {
                Id = Guid.NewGuid(), GoldenRecordId = goldenId, ProjectId = projectId, ClusterId = clusterId,
                IngestBatchId = Guid.NewGuid(), VersionNumber = 2,
                Fields = new Dictionary<string, string> { ["name"] = "Alice" }, CreatedAt = DateTimeOffset.UtcNow
            }
        ]);

        var (_, mutations) = Resolver.ClassifyAndDetachDeletions(
            project, Linkuity.Matching.MatchingDefaults.CreatePersonProfile(), ["d-1", "d-2"], Guid.NewGuid(), context, DateTimeOffset.UtcNow);

        Assert.Equal(2, mutations.VersionsToInsert.Count);
        Assert.Equal([3, 4], mutations.VersionsToInsert.Select(v => v.VersionNumber));
        Assert.All(mutations.VersionsToInsert, v => Assert.Equal(goldenId, v.GoldenRecordId));
    }

    [Fact]
    public void TwoDeletions_SameBatch_BothMembersOfTwoMemberCluster_TombstoneReflectsPendingReducedMembership()
    {
        var projectId = Guid.NewGuid();
        var project = MakeProject(projectId);
        var context = new InMemoryResolutionContext();

        var deletedOldId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var deletedOld = MakeRecord(projectId, "d-1", new Dictionary<string, string> { ["name"] = "Alice" }, deletedOldId);
        var other = MakeRecord(projectId, "d-2", new Dictionary<string, string> { ["name"] = "Bob" }, otherId);
        context.Records.AddRange([deletedOld, other]);

        var clusterId = Guid.NewGuid();
        context.Clusters.Add(new Cluster
        {
            Id = clusterId, ProjectId = projectId,
            MemberEntityRecordIds = [deletedOldId, otherId],
            CreatedAt = DateTimeOffset.UtcNow
        });

        var (deletedIds, mutations) = Resolver.ClassifyAndDetachDeletions(
            project, Linkuity.Matching.MatchingDefaults.CreatePersonProfile(), ["d-1", "d-2"], Guid.NewGuid(), context, DateTimeOffset.UtcNow);

        Assert.Equal(2, deletedIds.Count);

        var finalClusterState = mutations.ClustersToUpsert.Last(c => c.Id == clusterId);
        Assert.Equal("merged", finalClusterState.Status);
        Assert.Null(finalClusterState.MergedIntoClusterId);
        Assert.Equal([otherId], finalClusterState.MemberEntityRecordIds);
    }

    [Fact]
    public void NonexistentSourceRecordId_ThrowsInvalidOperationException()
    {
        var projectId = Guid.NewGuid();
        var project = MakeProject(projectId);
        var context = new InMemoryResolutionContext();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            Resolver.ClassifyAndDetachDeletions(
                project, Linkuity.Matching.MatchingDefaults.CreatePersonProfile(), ["missing-1"], Guid.NewGuid(), context, DateTimeOffset.UtcNow));

        Assert.Contains("missing-1", ex.Message);
    }
}
