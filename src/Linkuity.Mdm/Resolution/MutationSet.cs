using Linkuity.Core.Models;

namespace Linkuity.Mdm.Resolution;

// Targeted writes the store applies (all bounded by candidate/cluster fan-out + batch size).
public sealed class MutationSet
{
    public List<EntityRecord> RecordsToInsert { get; } = [];
    public List<MatchEdge> EdgesToInsert { get; } = [];
    public List<Cluster> ClustersToUpsert { get; } = [];                 // keyed by Id (new, replaced, tombstoned)
    public List<Guid> GoldenRecordClusterIdsToClear { get; } = [];       // loser clusters whose golden is removed
    public List<GoldenRecord> GoldenRecordsToUpsert { get; } = [];       // keyed by Id
    public List<GoldenRecordVersion> VersionsToInsert { get; } = [];
    public List<ReviewTask> ReviewTasksToInsert { get; } = [];
    public List<ClusterMergeEvent> MergeEventsToInsert { get; } = [];
}
