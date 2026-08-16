using Linkuity.Core.Models;

namespace Linkuity.Mdm.Resolution;

// Bounded reads. Implemented per backend.
public interface IResolutionContext
{
    // Only called on the no-index fallback path (File without an index / tests). Postgres always has an index.
    IReadOnlyList<EntityRecord> GetLinearCorpus(Guid projectId);
    IReadOnlyList<Cluster> GetActiveClustersContaining(Guid projectId, IReadOnlyCollection<Guid> recordIds);
    IReadOnlyList<EntityRecord> GetRecordsByIds(Guid projectId, IReadOnlyCollection<Guid> recordIds);
    IReadOnlyList<GoldenRecord> GetGoldenRecordsForClusters(Guid projectId, IReadOnlyCollection<Guid> clusterIds);
    IReadOnlyList<GoldenRecordVersion> GetVersionsForGoldenRecords(IReadOnlyCollection<Guid> goldenRecordIds);

    // The current (non-superseded, non-deleted) record for this natural key, or null if none
    // exists. Case-insensitive on sourceRecordId, matching the existing duplicate-key check it replaces.
    EntityRecord? FindCurrentRecordBySourceRecordId(Guid projectId, string sourceRecordId);
}
