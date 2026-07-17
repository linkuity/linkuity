using Linkuity.Core.Models;

namespace Linkuity.Mdm.Resolution;

// Promoted from FileMetadataStore.MetadataDatabase (same shape, now public).
public sealed class ResolutionWorkingSet
{
    public List<Project> Projects { get; init; } = [];
    public List<Source> Sources { get; init; } = [];
    public List<IngestBatch> IngestBatches { get; init; } = [];
    public List<EntityRecord> EntityRecords { get; init; } = [];
    public List<MatchEdge> MatchEdges { get; init; } = [];
    public List<Cluster> Clusters { get; init; } = [];
    public List<GoldenRecord> GoldenRecords { get; init; } = [];
    public List<GoldenRecordVersion> GoldenRecordVersions { get; init; } = [];
    public List<ReviewTask> ReviewTasks { get; init; } = [];
    public List<ClusterMergeEvent> ClusterMergeEvents { get; init; } = [];
}
