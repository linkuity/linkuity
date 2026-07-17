using Linkuity.Core.Models;
using Linkuity.Infrastructure.Local;
using Linkuity.Infrastructure.Lucene;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;

namespace Linkuity.Infrastructure.Local.Tests;

public class IncrementalIngestLuceneTests
{
    private static EntityRecord Record(Guid projectId, Guid sourceId, Guid batchId, string srid, Dictionary<string, string> fields) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        SourceId = sourceId,
        IngestBatchId = batchId,
        SourceRecordId = srid,
        Fields = fields,
        CreatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task DurablePath_WithLuceneSeam_AutoMatchesAndMaintainsIndex()
    {
        var root = Path.Combine(Path.GetTempPath(), "linkuity-luc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "metadata.json");
        var indexDir = Path.Combine(root, "index");
        var engine = MatchingDefaults.CreateEngine();
        var provider = new DefaultMatchingProfileProvider([DefaultMatchingProfileProvider.CreatePersonProfile()]);

        try
        {
            using var index = new LuceneCandidateRetrieval(new LuceneCandidateRetrievalOptions { IndexDirectory = indexDir });
            var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = dbPath }, engine, provider, index);
            var now = DateTimeOffset.UtcNow;
            var project = await store.CreateProjectAsync("MDM", "person", now, CancellationToken.None);
            var source = await store.CreateSourceAsync(project.Id, "CRM", now, CancellationToken.None);
            var seedBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, Guid.NewGuid(), 1, now, CancellationToken.None);
            var existing = Record(project.Id, source.Id, seedBatch.Id, "existing-1",
                new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "alice@example.com", ["name"] = "Alice" });
            await store.SaveCompletedBatchAsync(
                new CompletedBatchMetadata([existing], [],
                    [new Cluster { Id = Guid.NewGuid(), ProjectId = project.Id, MemberEntityRecordIds = [existing.Id], CreatedAt = now }],
                    [], []),
                CancellationToken.None);

            var incBatch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now.AddMinutes(1), CancellationToken.None);
            var incoming = Record(project.Id, source.Id, incBatch.Id, "in-1",
                new Dictionary<string, string> { ["source"] = "CRM", ["email"] = "alice@example.com", ["name"] = "Alice Verified" });

            var result = await store.SaveIncrementalIngestAsync(
                new IncrementalIngestRequest(project.Id, source.Id, incBatch.Id, [incoming], 0.90, 0.75), CancellationToken.None);

            Assert.Equal(1, result.AutoMatches);
            Assert.Equal(2, index.Count); // both records indexed
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
