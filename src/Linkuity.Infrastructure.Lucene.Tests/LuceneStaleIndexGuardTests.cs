using Linkuity.Matching.Profiles;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Store;
using Lucene.Net.Util;

namespace Linkuity.Infrastructure.Lucene.Tests;

/// <summary>
/// Project-scoped retrieval requires project_id to be an indexed term. Indexes written before
/// that change stored the value without indexing it, so every query against one matches nothing.
/// That failure is invisible from the outside — the index opens, retrieval succeeds, and the run
/// simply finds no duplicates — so it has to be turned into a loud error.
/// </summary>
public class LuceneStaleIndexGuardTests
{
    private const LuceneVersion Version = LuceneVersion.LUCENE_48;

    /// <summary>Writes an index in the pre-change shape: project_id stored, never indexed.</summary>
    private static void WriteLegacyIndex(string path, int documents)
    {
        using var directory = FSDirectory.Open(path);
        using var analyzer = new StandardAnalyzer(Version);
        using var writer = new IndexWriter(directory, new IndexWriterConfig(Version, analyzer));

        for (var i = 0; i < documents; i++)
        {
            writer.AddDocument(new Document
            {
                new StringField(LuceneFields.Id, Guid.NewGuid().ToString(), Field.Store.YES),
                new StoredField(LuceneFields.ProjectId, Guid.NewGuid().ToString()),
                new StringField(LuceneFields.BlockingKey, "email:someone@example.com", Field.Store.NO)
            });
        }

        writer.Commit();
    }

    [Fact]
    public void PopulatedLegacyIndex_IsRejectedWithRebuildInstruction()
    {
        var path = LuceneTestRecords.TempDir();
        WriteLegacyIndex(path, documents: 3);

        using var retrieval = new LuceneCandidateRetrieval(
            new LuceneCandidateRetrievalOptions { IndexDirectory = path });

        var incoming = LuceneTestRecords.Person(
            "in", new Dictionary<string, string> { ["email"] = "someone@example.com" });

        var error = Assert.Throws<InvalidOperationException>(
            () => retrieval.Retrieve(incoming, [], DefaultMatchingProfileProvider.CreatePersonProfile()));

        Assert.Contains("re-ingest", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An empty index has no terms for any field, so it must not be mistaken for a stale one —
    /// a fresh deployment opens exactly this index before its first record is written.
    /// </summary>
    [Fact]
    public void EmptyIndex_IsNotMistakenForStale()
    {
        var path = LuceneTestRecords.TempDir();
        WriteLegacyIndex(path, documents: 0);

        using var retrieval = new LuceneCandidateRetrieval(
            new LuceneCandidateRetrievalOptions { IndexDirectory = path });

        var incoming = LuceneTestRecords.Person(
            "in", new Dictionary<string, string> { ["email"] = "someone@example.com" });

        Assert.Empty(retrieval.Retrieve(incoming, [], DefaultMatchingProfileProvider.CreatePersonProfile()));
    }
}
