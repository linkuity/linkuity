namespace Linkuity.Core.Models;

public sealed record CompletedBatchMetadata(
    IReadOnlyList<EntityRecord> EntityRecords,
    IReadOnlyList<MatchEdge> MatchEdges,
    IReadOnlyList<Cluster> Clusters,
    IReadOnlyList<GoldenRecord> GoldenRecords,
    IReadOnlyList<GoldenRecordVersion> GoldenRecordVersions);
