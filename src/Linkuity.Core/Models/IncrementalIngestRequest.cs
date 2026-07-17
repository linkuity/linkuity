namespace Linkuity.Core.Models;

public sealed record IncrementalIngestRequest(
    Guid ProjectId,
    Guid SourceId,
    Guid IngestBatchId,
    IReadOnlyList<EntityRecord> Records,
    double AutoMatchThreshold,
    double ReviewThreshold);
