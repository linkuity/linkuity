using System.Diagnostics;
using Linkuity.Core.Interfaces;
using Linkuity.Core.Models;

namespace Linkuity.Mdm.Benchmarks;

/// <summary>
/// Runs an ingest measurement: creates a project, iterates over synthetic batches,
/// calls <see cref="IMetadataStore.SaveIncrementalIngestAsync"/> for each, and records
/// per-batch timing and memory metrics.
/// </summary>
public static class IngestMeasurement
{
    /// <summary>
    /// Ingests all <paramref name="batches"/> into the store returned by
    /// <paramref name="storeFactory"/> and returns a <see cref="MeasurementReport"/>
    /// with one <see cref="MeasurementRow"/> per batch.
    /// </summary>
    /// <remarks>
    /// The caller owns the store's lifecycle (index disposal, temp-dir cleanup).
    /// A fresh project is created for each call so runs are isolated.
    /// Sources are reused within a call when multiple batches share the same
    /// <see cref="SyntheticBatch.Source"/> name.
    /// </remarks>
    public static async Task<MeasurementReport> RunAsync(
        Func<IMetadataStore> storeFactory,
        IReadOnlyList<SyntheticBatch> batches,
        MeasurementSetup setup,
        CancellationToken ct = default)
    {
        var store = storeFactory();
        var now = DateTimeOffset.UtcNow;

        var project = await store.CreateProjectAsync(
            "benchmark", "person", mergeConfiguration: null, now, ct);

        var rows = new List<MeasurementRow>(batches.Count);
        var cumulative = 0;
        var sourcesByName = new Dictionary<string, Source>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < batches.Count; i++)
        {
            var batch = batches[i];

            if (!sourcesByName.TryGetValue(batch.Source, out var source))
            {
                source = await store.CreateSourceAsync(project.Id, batch.Source, now, ct);
                sourcesByName[batch.Source] = source;
            }

            var ingestBatch = await store.CreateIngestBatchAsync(
                project.Id, source.Id, jobId: null, batch.Records.Count, now, ct);

            var entityRecords = batch.Records.Select(r => new EntityRecord
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                SourceId = source.Id,
                IngestBatchId = ingestBatch.Id,
                SourceRecordId = r.SourceRecordId,
                Fields = r.Fields,
                CreatedAt = now,
            }).ToList();

            var sw = Stopwatch.StartNew();
            await store.SaveIncrementalIngestAsync(
                new IncrementalIngestRequest(
                    project.Id,
                    source.Id,
                    ingestBatch.Id,
                    entityRecords,
                    setup.AutoMatchThreshold,
                    setup.ReviewThreshold),
                ct);
            sw.Stop();

            cumulative += batch.Records.Count;
            var peakMb = Process.GetCurrentProcess().PeakWorkingSet64 / (1024.0 * 1024.0);

            rows.Add(new MeasurementRow(
                BatchIndex: i,
                RecordsInBatch: batch.Records.Count,
                CumulativeRecords: cumulative,
                ElapsedMs: sw.Elapsed.TotalMilliseconds,
                PeakWorkingSetMb: peakMb));

            // Stream occasional progress (stderr) so large end-to-end runs are observable live
            // instead of silent until the final report. See docs/performance-testing-plan.md.
            if ((i + 1) % 25 == 0 || i == batches.Count - 1)
                await Console.Error.WriteLineAsync(
                    $"[progress] batch {i + 1}/{batches.Count}  cumulative={cumulative}  " +
                    $"lastBatchMs={sw.Elapsed.TotalMilliseconds:F0}  peakWsMb={peakMb:F0}");
        }

        return new MeasurementReport(setup.Backend, rows);
    }
}
