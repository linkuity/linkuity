namespace Linkuity.Infrastructure.Postgres;

public sealed class PostgresMetadataStoreOptions
{
    public required string ConnectionString { get; init; }

    /// <summary>
    /// Degree of parallelism for the incremental-ingest matching loop. Default = ProcessorCount:
    /// after Milestone 26 (per-thread committed readers + leaner candidate reconstruction) parallel
    /// edge production scales (measured 3.33x vs sequential at 20 cores; see
    /// docs/roadmap/measurements/2026-07-05-ingest-retrieval-cost/). Set to 1 to force sequential.
    /// </summary>
    public int IngestParallelism { get; init; } = Environment.ProcessorCount;
}
