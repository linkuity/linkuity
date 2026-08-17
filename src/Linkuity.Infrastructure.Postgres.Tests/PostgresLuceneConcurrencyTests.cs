using Linkuity.Core.Models;
using Linkuity.Infrastructure.Lucene;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.TestSupport;

namespace Linkuity.Infrastructure.Postgres.Tests;

/// <summary>
/// PostgresMetadataStore has no analogue of FileMetadataStore's file-write gate — Postgres's own
/// transaction already protects the SQL side, but the shared LuceneCandidateRetrieval instance
/// (a DI singleton, see PostgresInfrastructureServiceCollectionExtensions) is not safe for
/// concurrent mutation, and its own class doc requires mutations not run concurrently with
/// Retrieve either. #66 widened the surface that touches it (corrections, deletions), so this
/// pins that concurrent calls against one store instance leave the index in a state consistent
/// with the durable store, rather than corrupted or dropped documents from an unsynchronized
/// shared IndexWriter.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresLuceneConcurrencyTests(SharedPostgresContainer shared) : IAsyncLifetime
{
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        if (!shared.IsAvailable)
            return; // tests will self-skip

        _connectionString = await shared.CreateDatabaseAsync(nameof(PostgresLuceneConcurrencyTests));
        DbUpMigrator.EnsureSchema(_connectionString);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static EntityRecord Record(Guid projectId, Guid sourceId, Guid batchId, string srid, Dictionary<string, string> fields, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        SourceId = sourceId,
        IngestBatchId = batchId,
        SourceRecordId = srid,
        Fields = fields,
        CreatedAt = at
    };

    [SkippableFact]
    public async Task ConcurrentCorrectionsAndDeletions_OnOneSharedStore_LeaveIndexConsistentWithDurableStore()
    {
        Skip.IfNot(shared.IsAvailable, "Docker not available — skipping Testcontainers test");

        var indexDir = Path.Combine(Path.GetTempPath(), "linkuity-pg-concurrency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(indexDir);
        try
        {
            using var index = new LuceneCandidateRetrieval(new LuceneCandidateRetrievalOptions { IndexDirectory = indexDir });
            var store = new PostgresMetadataStore(
                new PostgresMetadataStoreOptions { ConnectionString = _connectionString! },
                engine: null, profileProvider: null, indexedRetrieval: index);
            var now = DateTimeOffset.UtcNow;

            var project = await store.CreateProjectAsync("Customer MDM", "person", null, now);
            var source = await store.CreateSourceAsync(project.Id, "CRM", now);

            // 20 independent, unrelated records — distinct emails so no two ever legitimately
            // match each other. Half will be corrected, half deleted, all concurrently.
            const int recordCount = 20;
            var seedBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), recordCount, now);
            var seedRecords = Enumerable.Range(0, recordCount)
                .Select(i => Record(project.Id, source.Id, seedBatch.Id, $"crm-{i:000}",
                    new() { ["source"] = "CRM", ["email"] = $"person{i}@example.com" }, now))
                .ToList();
            await store.SaveIncrementalIngestAsync(
                new IncrementalIngestRequest(project.Id, source.Id, seedBatch.Id, seedRecords, 0.90, 0.75));

            var correctBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, recordCount / 2, now.AddMinutes(1));
            var deleteBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 0, now.AddMinutes(1));

            var tasks = new List<Task>();
            for (var i = 0; i < recordCount; i++)
            {
                if (i % 2 == 0)
                {
                    var corrected = Record(project.Id, source.Id, correctBatch.Id, $"crm-{i:000}",
                        new() { ["source"] = "CRM", ["email"] = $"person{i}-corrected@example.com" }, now.AddMinutes(1));
                    tasks.Add(store.SaveIncrementalIngestAsync(
                        new IncrementalIngestRequest(project.Id, source.Id, correctBatch.Id, [corrected], 0.90, 0.75)));
                }
                else
                {
                    tasks.Add(store.DeleteRecordsAsync(project.Id, source.Id, deleteBatch.Id, [$"crm-{i:000}"]));
                }
            }
            await Task.WhenAll(tasks);

            // Every operation must have applied cleanly (no lost/duplicated work from a
            // corrupted shared IndexWriter), and the index's live document count must match
            // what the durable store considers live.
            var live = await store.ListEntityRecordsAsync(project.Id);
            Assert.Equal(recordCount / 2, live.Count); // corrected records replace, deletions remove
            Assert.Equal(live.Count, index.Count);
        }
        finally
        {
            if (Directory.Exists(indexDir))
                Directory.Delete(indexDir, recursive: true);
        }
    }
}
