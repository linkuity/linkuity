using System.Diagnostics;
using Linkuity.Core.Models;
using Linkuity.Infrastructure.Lucene;
using Linkuity.Infrastructure.Postgres;
using Linkuity.TestSupport;
using Testcontainers.PostgreSql;

namespace Linkuity.Mdm.ConformanceTests;

/// <summary>
/// Proves that Postgres incremental ingest is bounded: per-batch elapsed time stays
/// roughly flat as cumulative project size grows, unlike the File store where time
/// rises ~O(N) due to whole-file rewrites.
///
/// Gated on Docker. Skipped (not failed) when Docker is unavailable.
/// </summary>
public sealed class PostgresBoundedIngestTests
{
    /*
     * Metric: per-batch elapsed_ms (Stopwatch around SaveIncrementalIngestAsync).
     *
     * Why not peak_working_set_mb?
     * Process.PeakWorkingSet64 is process-monotonic — it never decreases. A Postgres
     * run that starts low can only ever show flat-or-rising, making it useless for
     * proving that memory is bounded across batches.
     *
     * Why elapsed_ms?
     * Each Postgres batch issues a bounded set of parameterised SQL statements with
     * no full entity_records scan (structural guarantee from Task 13). Batch N at
     * cumulative record k should take no longer than batch N at cumulative record 0,
     * modulo constant Lucene-index overhead. elapsed_ms captures this directly.
     *
     * Tolerance: median(phase-2) ≤ 3× median(phase-1).
     * This is deliberately generous to absorb JIT warm-up variance, connection-pool
     * negotiation jitter, and Docker shared-resource variance on CI. The Task-13
     * structural no-full-scan guarantee is the primary proof; these timings corroborate.
     *
     * Counts:
     *   12 batches × 500 records = 6 000 total records.
     *   Phase 1 (batches 0–3):  500–2 000 cumulative records.
     *   Phase 2 (batches 8–11): 4 001–6 000 cumulative records.
     *   Ratio ≈ 3× in cumulative size, so a flat elapsed_ms is meaningful evidence.
     */

    [SkippableFact]
    public async Task PerBatchTime_StaysFlat_AsProjectGrows()
    {
        Skip.IfNot(DockerProbe.IsAvailable(), "Docker not available — skipping Testcontainers test");

        const int batchSize           = 500;
        const int phase1Count         = 4;   // early measurement batches (batches 0–3)
        const int warmupCount         = 4;   // unmeasured middle batches (batches 4–7)
        const int phase2Count         = 4;   // late measurement batches (batches 8–11)
        const double toleranceFactor  = 3.0; // median(phase2) ≤ 3× median(phase1)

        await using var h = await Harness.CreateAsync();
        var now = DateTimeOffset.UtcNow;

        var project = await h.Store.CreateProjectAsync("bounded-test", "person", null, now);
        var source  = await h.Store.CreateSourceAsync(project.Id, "BoundedSource", now);

        var rng           = new Random(42); // fixed seed → deterministic
        var recordCounter = 0;

        var phase1Elapsed = new List<double>(phase1Count);
        var phase2Elapsed = new List<double>(phase2Count);

        var totalBatches = phase1Count + warmupCount + phase2Count;

        for (var i = 0; i < totalBatches; i++)
        {
            var ingestBatch = await h.Store.CreateIngestBatchAsync(
                project.Id, source.Id, jobId: null, batchSize, now.AddMinutes(i));

            var records = MakeBatch(project.Id, source.Id, ingestBatch.Id,
                batchSize, ref recordCounter, rng, now.AddMinutes(i));

            var sw = Stopwatch.StartNew();
            await h.Store.SaveIncrementalIngestAsync(
                new IncrementalIngestRequest(project.Id, source.Id, ingestBatch.Id, records, 0.90, 0.75));
            sw.Stop();

            if (i < phase1Count)
                phase1Elapsed.Add(sw.Elapsed.TotalMilliseconds);
            else if (i >= phase1Count + warmupCount)
                phase2Elapsed.Add(sw.Elapsed.TotalMilliseconds);
        }

        var medianPhase1 = Median(phase1Elapsed);
        var medianPhase2 = Median(phase2Elapsed);

        Assert.True(
            medianPhase2 <= medianPhase1 * toleranceFactor,
            $"Postgres ingest NOT bounded: median late-batch {medianPhase2:F0} ms " +
            $"> {toleranceFactor}× median early-batch {medianPhase1:F0} ms. " +
            $"Phase-1 batches: [{string.Join(", ", phase1Elapsed.Select(x => $"{x:F0}"))}] ms. " +
            $"Phase-2 batches: [{string.Join(", ", phase2Elapsed.Select(x => $"{x:F0}"))}] ms.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static IReadOnlyList<EntityRecord> MakeBatch(
        Guid projectId, Guid sourceId, Guid batchId,
        int count, ref int counter, Random rng, DateTimeOffset at)
    {
        ReadOnlySpan<string> names =
        [
            "Alice Smith", "Bob Jones", "Carol White", "David Brown", "Eve Davis",
            "Frank Miller", "Grace Wilson", "Henry Moore", "Iris Taylor", "Jack Anderson",
        ];
        ReadOnlySpan<string> domains = ["gmail.com", "yahoo.com", "outlook.com"];

        var records = new List<EntityRecord>(count);
        for (var i = 0; i < count; i++)
        {
            var name   = names[rng.Next(names.Length)];
            var domain = domains[rng.Next(domains.Length)];
            var suffix = rng.Next(500); // limited range → ~20 % natural duplication rate

            records.Add(new EntityRecord
            {
                Id             = Guid.NewGuid(),
                ProjectId      = projectId,
                SourceId       = sourceId,
                IngestBatchId  = batchId,
                SourceRecordId = $"rec-{counter++:D7}",
                Fields         = new Dictionary<string, string>
                {
                    ["name"]  = name,
                    ["email"] = $"{name.Replace(" ", ".").ToLowerInvariant()}{suffix}@{domain}",
                },
                CreatedAt = at,
            });
        }
        return records;
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.OrderBy(x => x).ToList();
        var n = sorted.Count;
        return n % 2 == 0
            ? (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0
            : sorted[n / 2];
    }

    // ── Harness ──────────────────────────────────────────────────────────────────

    private sealed class Harness : IAsyncDisposable
    {
        public required PostgreSqlContainer  Container { get; init; }
        public required LuceneCandidateRetrieval Index { get; init; }
        public required PostgresMetadataStore    Store { get; init; }
        public required string                IndexDir { get; init; }

        public static async Task<Harness> CreateAsync()
        {
            var pg = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .Build();
            await pg.StartAsync();
            DbUpMigrator.EnsureSchema(pg.GetConnectionString());

            var indexDir = Path.Combine(
                Path.GetTempPath(), "linkuity-pg-bounded-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(indexDir);

            // FuzzyMaxEdits = 0 (exact blocking only) keeps candidate retrieval fast.
            var index = new LuceneCandidateRetrieval(
                new LuceneCandidateRetrievalOptions { IndexDirectory = indexDir, FuzzyMaxEdits = 0 });

            var store = new PostgresMetadataStore(
                new PostgresMetadataStoreOptions { ConnectionString = pg.GetConnectionString() },
                engine: null,
                profileProvider: null,
                indexedRetrieval: index);

            return new Harness { Container = pg, Index = index, Store = store, IndexDir = indexDir };
        }

        public async ValueTask DisposeAsync()
        {
            // PostgresMetadataStore owns Npgsql connection pools; dispose it before the
            // container shuts down. Cast through object so this compiles correctly even
            // though the sealed class currently implements neither interface — it will
            // work automatically if/when disposal is added to the store.
            object storeAsObj = Store;
            if (storeAsObj is IAsyncDisposable ad) await ad.DisposeAsync();
            else if (storeAsObj is IDisposable d) d.Dispose();
            Index.Dispose();
            await Container.DisposeAsync();
            try
            {
                if (Directory.Exists(IndexDir))
                    Directory.Delete(IndexDir, recursive: true);
            }
            catch
            {
                // Best-effort temp-dir cleanup.
            }
        }
    }
}
