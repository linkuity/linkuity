using Linkuity.Core.Models;

namespace Linkuity.Mdm.Resolution;

// Targeted writes the store applies (all bounded by candidate/cluster fan-out + batch size).
public sealed class MutationSet
{
    public List<EntityRecord> RecordsToInsert { get; } = [];

    // Upsert by Id — distinct from RecordsToInsert, which assumes the Id is always new. Used to
    // write back a record whose SupersededAt just got set (F6 correction), never for a brand-new one.
    public List<EntityRecord> RecordsToUpdate { get; } = [];

    public List<MatchEdge> EdgesToInsert { get; } = [];
    public List<Cluster> ClustersToUpsert { get; } = [];                 // keyed by Id (new, replaced, tombstoned)
    public List<Guid> GoldenRecordClusterIdsToClear { get; } = [];       // loser clusters whose golden is removed
    public List<GoldenRecord> GoldenRecordsToUpsert { get; } = [];       // keyed by Id
    public List<GoldenRecordVersion> VersionsToInsert { get; } = [];
    public List<ReviewTask> ReviewTasksToInsert { get; } = [];
    public List<ClusterMergeEvent> MergeEventsToInsert { get; } = [];

    // A component the merge policy refused. The audit trail for why a cluster did not form (or
    // did not survive re-evaluation) — see ClusterDissolutionEvent.
    public List<ClusterDissolutionEvent> DissolutionEventsToInsert { get; } = [];
    public List<RecordCorrectedEvent> CorrectionEventsToInsert { get; } = [];
}
