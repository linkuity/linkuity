using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Infrastructure.Lucene.Tests;

/// <summary>
/// One Lucene index can hold records for many projects. Retrieval must return only records
/// belonging to the querying record's project.
///
/// Filtering downstream of the search is not equivalent. Lucene selects its Top-N by relevance
/// across the whole index, so a foreign project's records occupy slots in that result set and
/// the true same-project matches never make it out of the search to be filtered. The loss is
/// silent: retrieval returns a full-looking result set that is simply missing the right records.
/// </summary>
public class LuceneProjectIsolationTests
{
    private static readonly IReadOnlyCollection<EntityRecord> NoCorpus = [];
    private static readonly MatchingProfile Profile = DefaultMatchingProfileProvider.CreatePersonProfile();

    private static readonly Guid ProjectA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ProjectB = new("bbbbbbbb-0000-0000-0000-000000000002");

    private static LuceneCandidateRetrieval NewIndex(int maxCandidates = 50)
        => new(new LuceneCandidateRetrievalOptions { IndexDirectory = LuceneTestRecords.TempDir(), MaxCandidates = maxCandidates });

    private static EntityRecord Person(Guid projectId, string id, string email)
        => LuceneTestRecords.Person(id, new Dictionary<string, string> { ["email"] = email }, projectId: projectId);

    [Fact]
    public void Retrieve_RecordsInAnotherProject_AreNotReturned()
    {
        using var index = NewIndex();
        index.Index(Person(ProjectB, "b1", "shared@example.com"));
        index.Index(Person(ProjectB, "b2", "shared@example.com"));
        index.Commit();

        var incoming = Person(ProjectA, "a1", "shared@example.com");

        Assert.Empty(index.Retrieve(incoming, NoCorpus, Profile));
    }

    [Fact]
    public void Retrieve_SameProjectRecords_AreStillReturned()
    {
        using var index = NewIndex();
        index.Index(Person(ProjectA, "a1", "shared@example.com"));
        index.Index(Person(ProjectA, "a2", "shared@example.com"));
        index.Commit();

        var incoming = Person(ProjectA, "a3", "shared@example.com");

        Assert.Equal(2, index.Retrieve(incoming, NoCorpus, Profile).Count);
    }

    /// <summary>
    /// The defect proper. A foreign project holds enough matching records to fill the Top-N
    /// budget on its own, so the one true same-project match was pushed out of the search
    /// results entirely and no amount of post-filtering could recover it.
    ///
    /// MaxBlockSize is raised well clear of the corpus so that suppression cannot fire and
    /// this test measures only Top-N competition. Suppression's own cross-project behaviour
    /// is a separate defect, documented below.
    /// </summary>
    [Fact]
    public void Retrieve_ForeignProjectFillsTopN_SameProjectMatchIsStillFound()
    {
        const int maxCandidates = 10;
        using var index = NewIndex(maxCandidates);

        for (var i = 0; i < maxCandidates * 3; i++)
            index.Index(Person(ProjectB, $"b{i}", "shared@example.com"));

        var trueMatch = Person(ProjectA, "a1", "shared@example.com");
        index.Index(trueMatch);
        index.Commit();

        var incoming = Person(ProjectA, "a2", "shared@example.com");
        var retrieved = index.Retrieve(incoming, NoCorpus, Profile with { MaxBlockSize = 1000 });

        var hit = Assert.Single(retrieved);
        Assert.Equal(trueMatch.Id, hit.Id);
    }

    /// <summary>
    /// Known defect, not fixed here. Block-size suppression asks Lucene for the term's
    /// DocFreq, which counts matching documents across the entire index regardless of
    /// project. A busy neighbouring project therefore inflates the apparent block size of
    /// keys in this one and can push them past MaxBlockSize, suppressing them outright — so
    /// a record retrieves nothing even though its own project holds a handful of records.
    ///
    /// Fixing it means counting hits for (project AND term) per key instead of an O(1)
    /// DocFreq lookup, which is a hot-path cost that needs measuring before it is taken on.
    /// </summary>
    [Fact]
    public void Suppression_ForeignProjectRecords_DoNotCountTowardThisProjectsBlockSize()
    {
        using var index = NewIndex();

        for (var i = 0; i < 30; i++)
            index.Index(Person(ProjectB, $"b{i}", "shared@example.com"));

        index.Index(Person(ProjectA, "a1", "shared@example.com"));
        index.Commit();

        var incoming = Person(ProjectA, "a2", "shared@example.com");

        // Project A's block holds two records, well under the threshold of 10, so the key
        // must stay active. Today project B's thirty records suppress it.
        Assert.Single(index.Retrieve(incoming, NoCorpus, Profile with { MaxBlockSize = 10 }));
    }
}
