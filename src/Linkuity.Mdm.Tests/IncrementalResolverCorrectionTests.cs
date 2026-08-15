using Linkuity.Core.Models;
using Linkuity.Mdm.Resolution;

namespace Linkuity.Mdm.Tests;

public class IncrementalResolverCorrectionTests
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

    private sealed class FakeContext : IResolutionContext
    {
        public List<EntityRecord> Records { get; } = [];
        public List<Cluster> Clusters { get; } = [];
        public List<GoldenRecord> GoldenRecords { get; } = [];
        public List<GoldenRecordVersion> Versions { get; } = [];

        public IReadOnlyList<EntityRecord> GetLinearCorpus(Guid projectId)
            => Records.Where(r => r.ProjectId == projectId && r.SupersededAt is null).ToList();

        public IReadOnlyList<Cluster> GetActiveClustersContaining(Guid projectId, IReadOnlyCollection<Guid> recordIds)
            => Clusters.Where(c => c.ProjectId == projectId && c.Status != "merged"
                                    && c.MemberEntityRecordIds.Any(recordIds.Contains)).ToList();

        public IReadOnlyList<EntityRecord> GetRecordsByIds(Guid projectId, IReadOnlyCollection<Guid> recordIds)
            => Records.Where(r => r.ProjectId == projectId && recordIds.Contains(r.Id) && r.SupersededAt is null).ToList();

        public IReadOnlyList<GoldenRecord> GetGoldenRecordsForClusters(Guid projectId, IReadOnlyCollection<Guid> clusterIds)
            => GoldenRecords.Where(g => g.ProjectId == projectId && clusterIds.Contains(g.ClusterId)).ToList();

        public IReadOnlyList<GoldenRecordVersion> GetVersionsForGoldenRecords(IReadOnlyCollection<Guid> goldenRecordIds)
            => Versions.Where(v => goldenRecordIds.Contains(v.GoldenRecordId)).ToList();

        public EntityRecord? FindCurrentRecordBySourceRecordId(Guid projectId, string sourceRecordId)
            => Records.FirstOrDefault(r => r.ProjectId == projectId && r.SupersededAt is null
                                            && string.Equals(r.SourceRecordId, sourceRecordId, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NewRecord_NoExistingMatch_PassesThroughUnchangedNoMutations()
    {
        var projectId = Guid.NewGuid();
        var project = MakeProject(projectId);
        var context = new FakeContext();
        var incoming = MakeRecord(projectId, "new-1", new Dictionary<string, string> { ["email"] = "a@example.com" });

        var (toResolve, mutations) = Resolver.ClassifyAndDetachCorrections(
            project, Linkuity.Matching.MatchingDefaults.CreatePersonProfile(), [incoming], context, DateTimeOffset.UtcNow);

        var resolved = Assert.Single(toResolve);
        Assert.Same(incoming, resolved);
        Assert.Empty(mutations.RecordsToUpdate);
        Assert.Empty(mutations.CorrectionEventsToInsert);
        Assert.Empty(mutations.ClustersToUpsert);
    }

    [Fact]
    public void ExistingRecord_IdenticalFields_IsDroppedAsNoOp()
    {
        var projectId = Guid.NewGuid();
        var project = MakeProject(projectId);
        var context = new FakeContext();
        var existingId = Guid.NewGuid();
        var existing = MakeRecord(projectId, "dup-1", new Dictionary<string, string> { ["email"] = "a@example.com" }, existingId);
        context.Records.Add(existing);
        var resend = MakeRecord(projectId, "dup-1", new Dictionary<string, string> { ["email"] = "a@example.com" });

        var (toResolve, mutations) = Resolver.ClassifyAndDetachCorrections(
            project, Linkuity.Matching.MatchingDefaults.CreatePersonProfile(), [resend], context, DateTimeOffset.UtcNow);

        Assert.Empty(toResolve);
        Assert.Empty(mutations.RecordsToUpdate);
        Assert.Empty(mutations.CorrectionEventsToInsert);
    }

    [Fact]
    public void ExistingRecord_DifferentFields_SingletonRecord_SupersedesWithNoDetach()
    {
        var projectId = Guid.NewGuid();
        var project = MakeProject(projectId);
        var context = new FakeContext();
        var existingId = Guid.NewGuid();
        var existing = MakeRecord(projectId, "s-1", new Dictionary<string, string> { ["email"] = "old@example.com" }, existingId);
        context.Records.Add(existing);
        // existing has no cluster in context.Clusters — it's an unclustered singleton.
        var resend = MakeRecord(projectId, "s-1", new Dictionary<string, string> { ["email"] = "new@example.com" });

        var (toResolve, mutations) = Resolver.ClassifyAndDetachCorrections(
            project, Linkuity.Matching.MatchingDefaults.CreatePersonProfile(), [resend], context, DateTimeOffset.UtcNow);

        var resolved = Assert.Single(toResolve);
        Assert.Same(resend, resolved);

        var superseded = Assert.Single(mutations.RecordsToUpdate);
        Assert.Equal(existingId, superseded.Id);
        Assert.NotNull(superseded.SupersededAt);
        Assert.Equal("old@example.com", superseded.Fields["email"]); // unchanged — only SupersededAt is new

        var evt = Assert.Single(mutations.CorrectionEventsToInsert);
        Assert.Equal(existingId, evt.SupersededEntityRecordId);
        Assert.Equal(resend.Id, evt.CorrectedEntityRecordId);
        Assert.Null(evt.PreviousClusterId); // nothing to detach from
        Assert.Empty(mutations.ClustersToUpsert); // nothing touched
    }

    [Fact]
    public void ExistingRecord_DifferentFields_MultiMemberCluster_SurvivorsKeepClusterIdentity()
    {
        var projectId = Guid.NewGuid();
        var merge = new MergeConfiguration { MergeFields = [] };
        var project = MakeProject(projectId, merge);
        var context = new FakeContext();

        var correctedOldId = Guid.NewGuid();
        var sibling1Id = Guid.NewGuid();
        var sibling2Id = Guid.NewGuid();
        var correctedOld = MakeRecord(projectId, "c-1", new Dictionary<string, string> { ["name"] = "Alice", ["city"] = "Bern" }, correctedOldId);
        var sibling1 = MakeRecord(projectId, "sib-1", new Dictionary<string, string> { ["name"] = "Alice", ["city"] = "Bern" }, sibling1Id);
        var sibling2 = MakeRecord(projectId, "sib-2", new Dictionary<string, string> { ["name"] = "Alice", ["city"] = "Bern" }, sibling2Id);
        context.Records.AddRange([correctedOld, sibling1, sibling2]);

        var clusterId = Guid.NewGuid();
        var cluster = new Cluster
        {
            Id = clusterId, ProjectId = projectId,
            MemberEntityRecordIds = [correctedOldId, sibling1Id, sibling2Id],
            CreatedAt = DateTimeOffset.UtcNow
        };
        context.Clusters.Add(cluster);
        var goldenId = Guid.NewGuid();
        context.GoldenRecords.Add(new GoldenRecord
        {
            Id = goldenId, ProjectId = projectId, ClusterId = clusterId, CurrentVersionId = Guid.NewGuid(),
            Fields = new Dictionary<string, string> { ["name"] = "Alice", ["city"] = "Bern" }, UpdatedAt = DateTimeOffset.UtcNow
        });

        var resend = MakeRecord(projectId, "c-1", new Dictionary<string, string> { ["name"] = "Zoe", ["city"] = "Oslo" });

        var (toResolve, mutations) = Resolver.ClassifyAndDetachCorrections(
            project, Linkuity.Matching.MatchingDefaults.CreatePersonProfile(), [resend], context, DateTimeOffset.UtcNow);

        Assert.Same(resend, Assert.Single(toResolve));

        var reducedCluster = Assert.Single(mutations.ClustersToUpsert);
        Assert.Equal(clusterId, reducedCluster.Id); // SAME cluster id — F28 identity retention
        // Order preserved from the original cluster's member list minus the corrected record —
        // not sorted, so compare against the raw list, not an OrderBy on only one side.
        Assert.Equal([sibling1Id, sibling2Id], reducedCluster.MemberEntityRecordIds);

        var recomputedGolden = Assert.Single(mutations.GoldenRecordsToUpsert);
        Assert.Equal(goldenId, recomputedGolden.Id); // SAME golden record id
        Assert.Equal(clusterId, recomputedGolden.ClusterId);
        Assert.Equal("Alice", recomputedGolden.Fields["name"]); // recomputed from the 2 survivors only
        Assert.Equal("Bern", recomputedGolden.Fields["city"]);

        var evt = Assert.Single(mutations.CorrectionEventsToInsert);
        Assert.Equal(clusterId, evt.PreviousClusterId);
    }

    [Fact]
    public void ExistingRecord_DifferentFields_TwoMemberCluster_SurvivorKeepsOriginalClusterId()
    {
        var projectId = Guid.NewGuid();
        var project = MakeProject(projectId);
        var context = new FakeContext();

        var correctedOldId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var correctedOld = MakeRecord(projectId, "c-1", new Dictionary<string, string> { ["name"] = "Alice" }, correctedOldId);
        var survivor = MakeRecord(projectId, "sib-1", new Dictionary<string, string> { ["name"] = "Alice" }, survivorId);
        context.Records.AddRange([correctedOld, survivor]);

        var clusterId = Guid.NewGuid();
        context.Clusters.Add(new Cluster
        {
            Id = clusterId, ProjectId = projectId, MemberEntityRecordIds = [correctedOldId, survivorId],
            CreatedAt = DateTimeOffset.UtcNow
        });

        var resend = MakeRecord(projectId, "c-1", new Dictionary<string, string> { ["name"] = "Zoe" });

        var (_, mutations) = Resolver.ClassifyAndDetachCorrections(
            project, Linkuity.Matching.MatchingDefaults.CreatePersonProfile(), [resend], context, DateTimeOffset.UtcNow);

        var reducedCluster = Assert.Single(mutations.ClustersToUpsert);
        Assert.Equal(clusterId, reducedCluster.Id); // still the original id, not a new one
        Assert.Equal([survivorId], reducedCluster.MemberEntityRecordIds);
        Assert.Equal("active", reducedCluster.Status); // NOT tombstoned — one member left is a valid cluster
    }

    [Fact]
    public void ExistingRecord_DifferentFields_ClusterHadOnlyThisMember_ClusterTombstonedGoldenCleared()
    {
        var projectId = Guid.NewGuid();
        var project = MakeProject(projectId);
        var context = new FakeContext();

        var oldId = Guid.NewGuid();
        var old = MakeRecord(projectId, "c-1", new Dictionary<string, string> { ["name"] = "Alice" }, oldId);
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

        var resend = MakeRecord(projectId, "c-1", new Dictionary<string, string> { ["name"] = "Zoe" });

        var (_, mutations) = Resolver.ClassifyAndDetachCorrections(
            project, Linkuity.Matching.MatchingDefaults.CreatePersonProfile(), [resend], context, DateTimeOffset.UtcNow);

        var tombstoned = Assert.Single(mutations.ClustersToUpsert);
        Assert.Equal(clusterId, tombstoned.Id);
        Assert.Equal("merged", tombstoned.Status);
        Assert.Null(tombstoned.MergedIntoClusterId);
        Assert.Equal(clusterId, Assert.Single(mutations.GoldenRecordClusterIdsToClear));
        Assert.Empty(mutations.GoldenRecordsToUpsert); // nothing to recompute — cluster is gone
    }

    [Fact]
    public void ExistingRecord_DifferentFields_GoldenRecordAlreadyVersioned_NextVersionNumberIsSequential()
    {
        var projectId = Guid.NewGuid();
        var merge = new MergeConfiguration { MergeFields = [] };
        var project = MakeProject(projectId, merge);
        var context = new FakeContext();

        var correctedOldId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var correctedOld = MakeRecord(projectId, "c-1", new Dictionary<string, string> { ["name"] = "Alice" }, correctedOldId);
        var survivor = MakeRecord(projectId, "sib-1", new Dictionary<string, string> { ["name"] = "Alice" }, survivorId);
        context.Records.AddRange([correctedOld, survivor]);

        var clusterId = Guid.NewGuid();
        context.Clusters.Add(new Cluster
        {
            Id = clusterId, ProjectId = projectId, MemberEntityRecordIds = [correctedOldId, survivorId],
            CreatedAt = DateTimeOffset.UtcNow
        });
        var goldenId = Guid.NewGuid();
        context.GoldenRecords.Add(new GoldenRecord
        {
            Id = goldenId, ProjectId = projectId, ClusterId = clusterId, CurrentVersionId = Guid.NewGuid(),
            Fields = new Dictionary<string, string> { ["name"] = "Alice" }, UpdatedAt = DateTimeOffset.UtcNow
        });
        // The golden record already has 2 prior versions — the next one must be 3, not 1.
        context.Versions.AddRange(
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

        var resend = MakeRecord(projectId, "c-1", new Dictionary<string, string> { ["name"] = "Zoe" });

        var (_, mutations) = Resolver.ClassifyAndDetachCorrections(
            project, Linkuity.Matching.MatchingDefaults.CreatePersonProfile(), [resend], context, DateTimeOffset.UtcNow);

        var newVersion = Assert.Single(mutations.VersionsToInsert);
        Assert.Equal(3, newVersion.VersionNumber);
    }

    [Fact]
    public void ExistingRecord_DifferentFields_RecomputedVersion_AttributedToCorrectingRecordsIngestBatch()
    {
        var projectId = Guid.NewGuid();
        var merge = new MergeConfiguration { MergeFields = [] };
        var project = MakeProject(projectId, merge);
        var context = new FakeContext();

        var correctedOldId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var correctedOld = MakeRecord(projectId, "c-1", new Dictionary<string, string> { ["name"] = "Alice" }, correctedOldId);
        var survivor = MakeRecord(projectId, "sib-1", new Dictionary<string, string> { ["name"] = "Alice" }, survivorId);
        context.Records.AddRange([correctedOld, survivor]);

        var clusterId = Guid.NewGuid();
        context.Clusters.Add(new Cluster
        {
            Id = clusterId, ProjectId = projectId, MemberEntityRecordIds = [correctedOldId, survivorId],
            CreatedAt = DateTimeOffset.UtcNow
        });
        var goldenId = Guid.NewGuid();
        context.GoldenRecords.Add(new GoldenRecord
        {
            Id = goldenId, ProjectId = projectId, ClusterId = clusterId, CurrentVersionId = Guid.NewGuid(),
            Fields = new Dictionary<string, string> { ["name"] = "Alice" }, UpdatedAt = DateTimeOffset.UtcNow
        });

        var resend = MakeRecord(projectId, "c-1", new Dictionary<string, string> { ["name"] = "Zoe" });

        var (_, mutations) = Resolver.ClassifyAndDetachCorrections(
            project, Linkuity.Matching.MatchingDefaults.CreatePersonProfile(), [resend], context, DateTimeOffset.UtcNow);

        var newVersion = Assert.Single(mutations.VersionsToInsert);
        // The correcting record's own batch, NOT correctedOld's (the superseded record's) batch.
        Assert.Equal(resend.IngestBatchId, newVersion.IngestBatchId);
        Assert.NotEqual(correctedOld.IngestBatchId, newVersion.IngestBatchId);
    }

    [Fact]
    public void TwoCorrections_SameBatch_SameCluster_BothDetachesReflectedInSurvivorList()
    {
        var projectId = Guid.NewGuid();
        var project = MakeProject(projectId);
        var context = new FakeContext();

        var correctedOld1Id = Guid.NewGuid();
        var correctedOld2Id = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var correctedOld1 = MakeRecord(projectId, "c-1", new Dictionary<string, string> { ["name"] = "Alice" }, correctedOld1Id);
        var correctedOld2 = MakeRecord(projectId, "c-2", new Dictionary<string, string> { ["name"] = "Bob" }, correctedOld2Id);
        var survivor = MakeRecord(projectId, "sib-1", new Dictionary<string, string> { ["name"] = "Carol" }, survivorId);
        context.Records.AddRange([correctedOld1, correctedOld2, survivor]);

        var clusterId = Guid.NewGuid();
        context.Clusters.Add(new Cluster
        {
            Id = clusterId, ProjectId = projectId,
            MemberEntityRecordIds = [correctedOld1Id, correctedOld2Id, survivorId],
            CreatedAt = DateTimeOffset.UtcNow
        });

        var resend1 = MakeRecord(projectId, "c-1", new Dictionary<string, string> { ["name"] = "Zoe" });
        var resend2 = MakeRecord(projectId, "c-2", new Dictionary<string, string> { ["name"] = "Yuri" });

        var (toResolve, mutations) = Resolver.ClassifyAndDetachCorrections(
            project, Linkuity.Matching.MatchingDefaults.CreatePersonProfile(), [resend1, resend2], context, DateTimeOffset.UtcNow);

        Assert.Equal(2, toResolve.Count);

        // Both records left the cluster in this one call — the FINAL entry for this cluster id must
        // reflect BOTH detaches, not just the last one processed.
        var finalClusterState = mutations.ClustersToUpsert.Last(c => c.Id == clusterId);
        Assert.Equal([survivorId], finalClusterState.MemberEntityRecordIds);
    }

    [Fact]
    public void TwoCorrections_SameBatch_SameGoldenRecord_VersionNumbersAreSequentialNotDuplicated()
    {
        var projectId = Guid.NewGuid();
        var merge = new MergeConfiguration { MergeFields = [] };
        var project = MakeProject(projectId, merge);
        var context = new FakeContext();

        var correctedOld1Id = Guid.NewGuid();
        var correctedOld2Id = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var correctedOld1 = MakeRecord(projectId, "c-1", new Dictionary<string, string> { ["name"] = "Alice" }, correctedOld1Id);
        var correctedOld2 = MakeRecord(projectId, "c-2", new Dictionary<string, string> { ["name"] = "Bob" }, correctedOld2Id);
        var survivor = MakeRecord(projectId, "sib-1", new Dictionary<string, string> { ["name"] = "Carol" }, survivorId);
        context.Records.AddRange([correctedOld1, correctedOld2, survivor]);

        var clusterId = Guid.NewGuid();
        context.Clusters.Add(new Cluster
        {
            Id = clusterId, ProjectId = projectId,
            MemberEntityRecordIds = [correctedOld1Id, correctedOld2Id, survivorId],
            CreatedAt = DateTimeOffset.UtcNow
        });
        var goldenId = Guid.NewGuid();
        context.GoldenRecords.Add(new GoldenRecord
        {
            Id = goldenId, ProjectId = projectId, ClusterId = clusterId, CurrentVersionId = Guid.NewGuid(),
            Fields = new Dictionary<string, string> { ["name"] = "Alice" }, UpdatedAt = DateTimeOffset.UtcNow
        });
        // The golden record already has 2 prior versions before this batch runs.
        context.Versions.AddRange(
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

        var resend1 = MakeRecord(projectId, "c-1", new Dictionary<string, string> { ["name"] = "Zoe" });
        var resend2 = MakeRecord(projectId, "c-2", new Dictionary<string, string> { ["name"] = "Yuri" });

        var (_, mutations) = Resolver.ClassifyAndDetachCorrections(
            project, Linkuity.Matching.MatchingDefaults.CreatePersonProfile(), [resend1, resend2], context, DateTimeOffset.UtcNow);

        // Two corrections in this one batch both recompute the SAME golden record — the resulting
        // versions must be sequential (3, then 4), not both "3" (each independently reading the
        // context's still-stale, pre-batch count of 2).
        Assert.Equal(2, mutations.VersionsToInsert.Count);
        Assert.Equal([3, 4], mutations.VersionsToInsert.Select(v => v.VersionNumber));
        Assert.All(mutations.VersionsToInsert, v => Assert.Equal(goldenId, v.GoldenRecordId));
    }

    [Fact]
    public void TwoCorrections_SameBatch_BothMembersOfTwoMemberCluster_TombstoneReflectsPendingReducedMembership()
    {
        // Regression for #68: cluster {A,B} with both members corrected in the SAME batch.
        // Iteration 1 (correcting A) reduces the cluster to the survivor {B} via the "survivors
        // keep cluster id" branch, which correctly writes the PENDING-reduced membership
        // ([survivorId]), not the stale pre-batch list. Iteration 2 (correcting B, now the
        // cluster's only remaining member) takes the tombstone branch — its
        // MemberEntityRecordIds must be consistent with the sibling branch's convention: the
        // membership as reduced by THIS BATCH SO FAR ([survivorId], i.e. B alone, since A
        // already left earlier in this same batch), not cluster.MemberEntityRecordIds (the
        // ORIGINAL, pre-batch [correctedOldId, survivorId]) — which would restate A as still a
        // member of a cluster A left several lines of this same batch earlier.
        var projectId = Guid.NewGuid();
        var project = MakeProject(projectId);
        var context = new FakeContext();

        var correctedOldId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var correctedOld = MakeRecord(projectId, "c-1", new Dictionary<string, string> { ["name"] = "Alice" }, correctedOldId);
        var survivor = MakeRecord(projectId, "c-2", new Dictionary<string, string> { ["name"] = "Bob" }, survivorId);
        context.Records.AddRange([correctedOld, survivor]);

        var clusterId = Guid.NewGuid();
        context.Clusters.Add(new Cluster
        {
            Id = clusterId, ProjectId = projectId,
            MemberEntityRecordIds = [correctedOldId, survivorId],
            CreatedAt = DateTimeOffset.UtcNow
        });

        var resend1 = MakeRecord(projectId, "c-1", new Dictionary<string, string> { ["name"] = "Zoe" });
        var resend2 = MakeRecord(projectId, "c-2", new Dictionary<string, string> { ["name"] = "Yuri" });

        var (toResolve, mutations) = Resolver.ClassifyAndDetachCorrections(
            project, Linkuity.Matching.MatchingDefaults.CreatePersonProfile(), [resend1, resend2], context, DateTimeOffset.UtcNow);

        Assert.Equal(2, toResolve.Count);

        // The FINAL entry for this cluster id must be the tombstone (both members left).
        var finalClusterState = mutations.ClustersToUpsert.Last(c => c.Id == clusterId);
        Assert.Equal("merged", finalClusterState.Status);
        Assert.Null(finalClusterState.MergedIntoClusterId);
        // Must reflect the pending-reduced membership at the moment of tombstoning ([survivorId] —
        // B alone), consistent with the survivor branch's own convention, NOT the stale original
        // [correctedOldId, survivorId] that includes A, who already left earlier in this batch.
        Assert.Equal([survivorId], finalClusterState.MemberEntityRecordIds);
    }
}
