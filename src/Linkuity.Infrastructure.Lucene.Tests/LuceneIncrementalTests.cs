using Linkuity.Core.Models;
using Linkuity.Infrastructure.Lucene;
using Linkuity.Matching.Profiles;

namespace Linkuity.Infrastructure.Lucene.Tests;

public class LuceneIncrementalTests
{
    private static readonly MatchingProfile Profile = DefaultMatchingProfileProvider.CreatePersonProfile();
    private static readonly IReadOnlyCollection<EntityRecord> NoCorpus = [];

    [Fact]
    public void Update_ChangesWhichQueriesMatchTheRecord()
    {
        using var index = new LuceneCandidateRetrieval(new LuceneCandidateRetrievalOptions { IndexDirectory = LuceneTestRecords.TempDir() });
        var id = Guid.NewGuid();
        index.Index(LuceneTestRecords.Person("a", new Dictionary<string, string> { ["email"] = "old@example.com" }, id));
        index.Commit();

        // Same id, new email -> the old email must no longer retrieve it; the new one must.
        index.Update(LuceneTestRecords.Person("a", new Dictionary<string, string> { ["email"] = "new@example.com" }, id));
        index.Commit();

        var byOld = index.Retrieve(LuceneTestRecords.Person("q", new Dictionary<string, string> { ["email"] = "old@example.com" }), NoCorpus, Profile);
        var byNew = index.Retrieve(LuceneTestRecords.Person("q", new Dictionary<string, string> { ["email"] = "new@example.com" }), NoCorpus, Profile);

        Assert.DoesNotContain(byOld, c => c.Id == id);
        Assert.Contains(byNew, c => c.Id == id);
    }

    [Fact]
    public void Update_DoesNotDuplicateTheDocument()
    {
        using var index = new LuceneCandidateRetrieval(new LuceneCandidateRetrievalOptions { IndexDirectory = LuceneTestRecords.TempDir() });
        var id = Guid.NewGuid();
        index.Index(LuceneTestRecords.Person("a", new Dictionary<string, string> { ["email"] = "x@example.com" }, id));
        index.Update(LuceneTestRecords.Person("a", new Dictionary<string, string> { ["email"] = "x@example.com" }, id));
        index.Commit();

        var hits = index.Retrieve(LuceneTestRecords.Person("q", new Dictionary<string, string> { ["email"] = "x@example.com" }), NoCorpus, Profile);
        Assert.Single(hits, c => c.Id == id);
    }

    [Fact]
    public void Remove_DropsRecordFromResults()
    {
        using var index = new LuceneCandidateRetrieval(new LuceneCandidateRetrievalOptions { IndexDirectory = LuceneTestRecords.TempDir() });
        var id = Guid.NewGuid();
        index.Index(LuceneTestRecords.Person("a", new Dictionary<string, string> { ["email"] = "gone@example.com" }, id));
        index.Commit();

        index.Remove(id);
        index.Commit();

        var hits = index.Retrieve(LuceneTestRecords.Person("q", new Dictionary<string, string> { ["email"] = "gone@example.com" }), NoCorpus, Profile);
        Assert.DoesNotContain(hits, c => c.Id == id);
    }
}
