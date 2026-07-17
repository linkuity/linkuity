using Linkuity.Core.Models;

namespace Linkuity.Mdm.Resolution;

/// <summary>
/// Persistence-agnostic completed-batch resolution, extracted verbatim from
/// <c>FileMetadataStore.SaveCompletedBatchAsync</c> / <c>ApplyCompletedBatchGoldenRecords</c>.
/// Emits a <see cref="MutationSet"/> the backend applies in its own transaction.
/// </summary>
public static class CompletedBatchResolver
{
    public static MutationSet Resolve(
        CompletedBatchMetadata batch,
        IReadOnlyList<Project> projects,
        DateTimeOffset now)
    {
        var mutations = new MutationSet();
        mutations.RecordsToInsert.AddRange(batch.EntityRecords);
        mutations.EdgesToInsert.AddRange(batch.MatchEdges);
        mutations.ClustersToUpsert.AddRange(batch.Clusters);

        var recordsById = batch.EntityRecords.ToDictionary(r => r.Id);
        var recomputedClusterIds = new HashSet<Guid>();

        foreach (var cluster in batch.Clusters)
        {
            var project = projects.First(p => p.Id == cluster.ProjectId);
            if (project.MergeConfiguration is null)
                continue;

            var members = cluster.MemberEntityRecordIds
                .Where(recordsById.ContainsKey)
                .Select(id => recordsById[id])
                .ToList();
            if (members.Count == 0)
                throw new InvalidOperationException("Cluster must contain at least one entity record when project merge policy is applied.");

            var ingestBatchIds = members
                .Select(m => m.IngestBatchId)
                .Distinct()
                .ToList();
            if (ingestBatchIds.Count != 1)
                throw new InvalidOperationException("Cluster members must belong to exactly one ingest batch when project merge policy is applied.");

            var fields = GoldenRecordMerge.MergeFields(project, members);
            var goldenRecordId = Guid.NewGuid();
            var versionId = Guid.NewGuid();

            mutations.GoldenRecordsToUpsert.Add(new GoldenRecord
            {
                Id = goldenRecordId,
                ProjectId = cluster.ProjectId,
                ClusterId = cluster.Id,
                CurrentVersionId = versionId,
                Fields = fields,
                UpdatedAt = now
            });
            mutations.VersionsToInsert.Add(new GoldenRecordVersion
            {
                Id = versionId,
                GoldenRecordId = goldenRecordId,
                ProjectId = cluster.ProjectId,
                ClusterId = cluster.Id,
                IngestBatchId = ingestBatchIds[0],
                VersionNumber = 1,
                Fields = fields,
                CreatedAt = now
            });
            recomputedClusterIds.Add(cluster.Id);
        }

        // Preserve branch: pass through goldens/versions for clusters that had no merge policy.
        var preservedGoldenRecords = batch.GoldenRecords
            .Where(golden => !recomputedClusterIds.Contains(golden.ClusterId))
            .ToList();
        var preservedGoldenIds = preservedGoldenRecords.Select(g => g.Id).ToHashSet();
        mutations.GoldenRecordsToUpsert.AddRange(preservedGoldenRecords);
        mutations.VersionsToInsert.AddRange(batch.GoldenRecordVersions.Where(v => preservedGoldenIds.Contains(v.GoldenRecordId)));

        // GoldenRecordClusterIdsToClear stays empty: completed-batch clusters are new,
        // so the prior RemoveAll(ProjectId && ClusterId) in the source was a no-op.
        return mutations;
    }
}
