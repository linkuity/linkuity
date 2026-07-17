namespace Linkuity.Core.Models;

public sealed record IncrementalIngestResult(
    int RecordsAdded,
    int AutoMatches,
    int ReviewTasks,
    int SingletonClusters,
    int GoldenRecordVersionsCreated);
