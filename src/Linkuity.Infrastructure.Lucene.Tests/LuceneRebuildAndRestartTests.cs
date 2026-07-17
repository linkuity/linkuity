using Linkuity.Core.Models;
using Linkuity.Infrastructure.Lucene;
using Linkuity.Matching.Profiles;

namespace Linkuity.Infrastructure.Lucene.Tests;

public class LuceneRebuildAndRestartTests
{
    private static readonly MatchingProfile Profile = DefaultMatchingProfileProvider.CreatePersonProfile();
    private static readonly IReadOnlyCollection<EntityRecord> NoCorpus = [];

    private static IReadOnlyList<EntityRecord> Sample() =>
    [
        LuceneTestRecords.Person("a", new Dictionary<string, string> { ["last_name"] = "Smith", ["email"] = "alice@example.com" }),
        LuceneTestRecords.Person("b", new Dictionary<string, string> { ["last_name"] = "Jones", ["email"] = "bob@example.com" }),
        LuceneTestRecords.Person("c", new Dictionary<string, string> { ["last_name"] = "Smith", ["email"] = "carol@example.com" })
    ];

    private static HashSet<Guid> RetrieveIds(LuceneCandidateRetrieval index, EntityRecord query)
        => index.Retrieve(query, NoCorpus, Profile).Select(c => c.Id).ToHashSet();

    [Fact]
    public void Rebuild_ProducesSameRetrievalSetAsIncrementalIndexing()
    {
        var records = Sample();
        var query = LuceneTestRecords.Person("q", new Dictionary<string, string> { ["last_name"] = "Smith" });

        HashSet<Guid> incremental;
        using (var a = new LuceneCandidateRetrieval(new LuceneCandidateRetrievalOptions { IndexDirectory = LuceneTestRecords.TempDir() }))
        {
            foreach (var r in records) a.Index(r);
            a.Commit();
            incremental = RetrieveIds(a, query);
        }

        HashSet<Guid> rebuilt;
        using (var b = new LuceneCandidateRetrieval(new LuceneCandidateRetrievalOptions { IndexDirectory = LuceneTestRecords.TempDir() }))
        {
            b.Rebuild(records);
            b.Commit();
            rebuilt = RetrieveIds(b, query);
        }

        Assert.NotEmpty(incremental);
        Assert.Equal(incremental, rebuilt);
    }

    [Fact]
    public void Rebuild_ReplacesPriorContents()
    {
        var dir = LuceneTestRecords.TempDir();
        var stale = LuceneTestRecords.Person("stale", new Dictionary<string, string> { ["email"] = "stale@example.com" });
        using var index = new LuceneCandidateRetrieval(new LuceneCandidateRetrievalOptions { IndexDirectory = dir });
        index.Index(stale);
        index.Commit();

        index.Rebuild(Sample());
        index.Commit();

        var hits = index.Retrieve(LuceneTestRecords.Person("q", new Dictionary<string, string> { ["email"] = "stale@example.com" }), NoCorpus, Profile);
        Assert.DoesNotContain(hits, c => c.Id == stale.Id);
    }

    [Fact]
    public void Index_SurvivesReopenOnSameDirectory()
    {
        var dir = LuceneTestRecords.TempDir();
        var record = LuceneTestRecords.Person("a", new Dictionary<string, string> { ["email"] = "persist@example.com" });

        using (var first = new LuceneCandidateRetrieval(new LuceneCandidateRetrievalOptions { IndexDirectory = dir }))
        {
            first.Index(record);
            first.Commit();
        } // dispose closes the writer

        using var reopened = new LuceneCandidateRetrieval(new LuceneCandidateRetrievalOptions { IndexDirectory = dir });
        var hits = reopened.Retrieve(LuceneTestRecords.Person("q", new Dictionary<string, string> { ["email"] = "persist@example.com" }), NoCorpus, Profile);

        Assert.Contains(hits, c => c.Id == record.Id);
    }
}
