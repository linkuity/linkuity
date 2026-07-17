using Linkuity.Core.Models;
using Linkuity.Infrastructure.Lucene;
using Linkuity.Matching.Profiles;

namespace Linkuity.Infrastructure.Lucene.Tests;

/// <summary>
/// Guards the reader-lifecycle optimization: <see cref="LuceneCandidateRetrieval.Retrieve"/>
/// must reuse a cached reader across calls (not reopen per record), while, after an intervening
/// <see cref="LuceneCandidateRetrieval.Commit"/>, the next retrieval sees the new writes
/// (committed-reader visibility). See task-6-brief.md.
/// </summary>
public class LuceneReaderCachingTests
{
    private static readonly MatchingProfile Profile = DefaultMatchingProfileProvider.CreatePersonProfile();
    private static readonly IReadOnlyCollection<EntityRecord> NoCorpus = [];

    private static LuceneCandidateRetrieval NewIndex()
        => new(new LuceneCandidateRetrievalOptions { IndexDirectory = LuceneTestRecords.TempDir() });

    [Fact]
    public void Retrieve_DoesNotReopenReader_WhenIndexUnchanged()
    {
        using var index = NewIndex();
        index.Index(LuceneTestRecords.Person("a", new Dictionary<string, string> { ["last_name"] = "Smith" }));
        index.Index(LuceneTestRecords.Person("b", new Dictionary<string, string> { ["last_name"] = "Smith" }));
        index.Commit();

        var incoming = LuceneTestRecords.Person("c", new Dictionary<string, string> { ["last_name"] = "Smith" });

        // Prime the reader, then retrieve repeatedly with NO intervening index mutation.
        _ = index.Retrieve(incoming, NoCorpus, Profile);
        var reopensAfterPrime = index.ReopenCount;

        for (var i = 0; i < 5; i++)
            _ = index.Retrieve(incoming, NoCorpus, Profile);

        // A cached reader must not reopen while the index is unchanged: at most one more reopen
        // (there should be zero further reopens). Today's per-call DirectoryReader.Open makes this
        // rise by 5 → this asserts the caching behavior.
        Assert.True(
            index.ReopenCount - reopensAfterPrime <= 1,
            $"expected the reader not to reopen for an unchanged index, but ReopenCount rose by {index.ReopenCount - reopensAfterPrime} over 5 retrievals");
    }

    [Fact]
    public void Retrieve_SeesNewlyIndexedRecords_AfterReopen()
    {
        using var index = NewIndex();

        var a = LuceneTestRecords.Person("a", new Dictionary<string, string> { ["last_name"] = "Smith" });
        index.Index(a);
        index.Commit();

        var incoming = LuceneTestRecords.Person("c", new Dictionary<string, string> { ["last_name"] = "Smith" });

        // First retrieval sees A (and primes the cached reader).
        var first = index.Retrieve(incoming, NoCorpus, Profile);
        Assert.Contains(first, r => r.Id == a.Id);

        // Index a second matching record AFTER the reader was primed — the durable-ingest pattern
        // where records are indexed after each batch's matching loop.
        var b = LuceneTestRecords.Person("b", new Dictionary<string, string> { ["last_name"] = "Smith" });
        index.Index(b);
        index.Commit();

        // The next retrieval must observe B (cached reader must reopen-on-change, not serve stale).
        var second = index.Retrieve(incoming, NoCorpus, Profile);
        Assert.Contains(second, r => r.Id == a.Id);
        Assert.Contains(second, r => r.Id == b.Id);
    }
}
