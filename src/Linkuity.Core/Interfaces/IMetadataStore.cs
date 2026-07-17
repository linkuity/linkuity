using Linkuity.Core.Models;

namespace Linkuity.Core.Interfaces;

public interface IMetadataStore
{
    Task<Project> CreateProjectAsync(string name, string contentType, MergeConfiguration? mergeConfiguration, DateTimeOffset createdAt, CancellationToken ct = default);
    Task<Project> UpdateProjectMergePolicyAsync(Guid projectId, MergeConfiguration? mergeConfiguration, CancellationToken ct = default);
    Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct = default);
    Task<Project?> GetProjectAsync(Guid projectId, CancellationToken ct = default);
    Task<Source> CreateSourceAsync(Guid projectId, string name, DateTimeOffset createdAt, CancellationToken ct = default);
    Task<IReadOnlyList<Source>> ListSourcesAsync(Guid projectId, CancellationToken ct = default);
    Task<Source?> GetSourceAsync(Guid sourceId, CancellationToken ct = default);
    Task<IngestBatch> CreateIngestBatchAsync(Guid projectId, Guid sourceId, Guid? jobId, int recordCount, DateTimeOffset createdAt, CancellationToken ct = default);
    Task<IReadOnlyList<IngestBatch>> ListIngestBatchesAsync(Guid projectId, CancellationToken ct = default);
    Task SaveCompletedBatchAsync(CompletedBatchMetadata completedBatch, CancellationToken ct = default);
    Task<IncrementalIngestResult> SaveIncrementalIngestAsync(IncrementalIngestRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<EntityRecord>> ListEntityRecordsAsync(Guid projectId, CancellationToken ct = default);
    Task<IReadOnlyList<MatchEdge>> ListMatchEdgesAsync(Guid projectId, CancellationToken ct = default);
    Task<IReadOnlyList<Cluster>> ListClustersAsync(Guid projectId, CancellationToken ct = default);
    Task<IReadOnlyList<GoldenRecord>> ListGoldenRecordsAsync(Guid projectId, CancellationToken ct = default);
    Task<IReadOnlyList<GoldenRecordVersion>> ListGoldenRecordVersionsAsync(Guid projectId, CancellationToken ct = default);
    Task<IReadOnlyList<ReviewTask>> ListReviewTasksAsync(Guid projectId, CancellationToken ct = default);
    Task<IReadOnlyList<ClusterMergeEvent>> ListClusterMergeEventsAsync(Guid projectId, CancellationToken ct = default);
}
