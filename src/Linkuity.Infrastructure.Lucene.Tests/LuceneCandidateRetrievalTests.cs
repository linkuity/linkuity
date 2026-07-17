using Linkuity.Core.Models;
using Linkuity.Infrastructure.Lucene;
using Linkuity.Matching.Profiles;

namespace Linkuity.Infrastructure.Lucene.Tests;

public class LuceneCandidateRetrievalTests
{
    private static readonly MatchingProfile Profile = DefaultMatchingProfileProvider.CreatePersonProfile();
    private static readonly IReadOnlyCollection<EntityRecord> NoCorpus = [];

    private static LuceneCandidateRetrieval NewIndex(out string dir)
    {
        dir = LuceneTestRecords.TempDir();
        return new LuceneCandidateRetrieval(new LuceneCandidateRetrievalOptions { IndexDirectory = dir });
    }

    [Fact]
    public void Retrieve_ReturnsRecordSharingAnExactBlockingKey()
    {
        using var index = NewIndex(out _);
        var existing = LuceneTestRecords.Person("a", new Dictionary<string, string> { ["email"] = "alice@example.com", ["last_name"] = "Smith" });
        var unrelated = LuceneTestRecords.Person("b", new Dictionary<string, string> { ["email"] = "bob@example.com", ["last_name"] = "Jones" });
        index.Index(existing);
        index.Index(unrelated);
        index.Commit();

        var incoming = LuceneTestRecords.Person("c", new Dictionary<string, string> { ["email"] = "alice@example.com", ["last_name"] = "Smith" });
        var candidates = index.Retrieve(incoming, NoCorpus, Profile);

        Assert.Contains(candidates, c => c.Id == existing.Id);
        Assert.DoesNotContain(candidates, c => c.Id == unrelated.Id);
    }

    [Fact]
    public void Retrieve_IgnoresCorpusArgument_QueriesTheIndex()
    {
        using var index = NewIndex(out _);
        var indexed = LuceneTestRecords.Person("a", new Dictionary<string, string> { ["email"] = "shared@example.com" });
        index.Index(indexed);
        index.Commit();

        // A non-empty corpus full of decoys must not leak into results: retrieval is index-only.
        var decoy = LuceneTestRecords.Person("z", new Dictionary<string, string> { ["email"] = "shared@example.com" });
        var incoming = LuceneTestRecords.Person("c", new Dictionary<string, string> { ["email"] = "shared@example.com" });

        var candidates = index.Retrieve(incoming, new[] { decoy }, Profile);

        Assert.Contains(candidates, c => c.Id == indexed.Id);
        Assert.DoesNotContain(candidates, c => c.Id == decoy.Id);
    }

    [Fact]
    public void Retrieve_ReconstructsCandidateFields()
    {
        using var index = NewIndex(out _);
        var existing = LuceneTestRecords.Person("a", new Dictionary<string, string> { ["email"] = "alice@example.com", ["first_name"] = "Alice" });
        index.Index(existing);
        index.Commit();

        var incoming = LuceneTestRecords.Person("c", new Dictionary<string, string> { ["email"] = "alice@example.com" });
        var candidate = Assert.Single(index.Retrieve(incoming, NoCorpus, Profile), c => c.Id == existing.Id);

        Assert.Equal("Alice", candidate.Fields["first_name"]);
    }

    [Fact]
    public void Retrieve_NoSharedTerms_ReturnsEmpty()
    {
        using var index = NewIndex(out _);
        index.Index(LuceneTestRecords.Person("a", new Dictionary<string, string> { ["email"] = "alice@example.com" }));
        index.Commit();

        var incoming = LuceneTestRecords.Person("c", new Dictionary<string, string> { ["email"] = "nomatch@elsewhere.com" });
        Assert.Empty(index.Retrieve(incoming, NoCorpus, Profile));
    }

    [Fact]
    public void Retrieve_FindsPhoneticVariant_SharingOnlyAPhoneticKey()
    {
        using var index = NewIndex(out _);
        var existing = LuceneTestRecords.Person("a", new Dictionary<string, string> { ["last_name"] = "Smith" });
        index.Index(existing);
        index.Commit();

        // "Smith" and "Smyth" share a Double-Metaphone phonetic key (SM0), which is their only
        // shared blocking key and is indexed as an exact blocking_key term. This test asserts
        // that phonetic variants are retrieved end-to-end via that shared phonetic blocking key.
        var incoming = LuceneTestRecords.Person("c", new Dictionary<string, string> { ["last_name"] = "Smyth" });
        var candidates = index.Retrieve(incoming, NoCorpus, Profile);

        Assert.Contains(candidates, c => c.Id == existing.Id);
    }

    [Fact]
    public void Retrieve_FindsFuzzyTypo_OnNameField()
    {
        using var index = NewIndex(out _);
        var existing = LuceneTestRecords.Person("a", new Dictionary<string, string> { ["last_name"] = "Anderson" });
        index.Index(existing);
        index.Commit();

        // "Andersen": one edit from "Anderson"; the name fuzzy clause retrieves it.
        var incoming = LuceneTestRecords.Person("c", new Dictionary<string, string> { ["last_name"] = "Andersen" });
        var candidates = index.Retrieve(incoming, NoCorpus, Profile);

        Assert.Contains(candidates, c => c.Id == existing.Id);
    }

    [Fact]
    public void Retrieve_FuzzyNameClause_IsLoadBearing_IsolatedBlockingKeys()
    {
        // This test uses CONTROLLED BlockingKeys (not LuceneTestRecords.Person) so that the
        // indexed record and the incoming record share NO exact blocking_key term:
        //   "name:anderson" != "name:andersen"  (one-edit distance, o->e)
        // The only path from the incoming query to the indexed record is the FuzzyQuery on the
        // "name" field (edit distance 1, within FuzzyMaxEdits=2). This test would fail if the
        // fuzzy clause in CandidateQueryBuilder.Build were removed.
        using var index = NewIndex(out _);

        var sharedGuids = (ProjectId: Guid.NewGuid(), SourceId: Guid.NewGuid(), BatchId: Guid.NewGuid());

        var indexed = new EntityRecord
        {
            Id = Guid.NewGuid(),
            ProjectId = sharedGuids.ProjectId,
            SourceId = sharedGuids.SourceId,
            IngestBatchId = sharedGuids.BatchId,
            SourceRecordId = "fuzzy-indexed",
            Fields = new Dictionary<string, string> { ["last_name"] = "Anderson" },
            BlockingKeys = ["name:anderson"],
            CreatedAt = DateTimeOffset.UtcNow
        };

        var noise = new EntityRecord
        {
            Id = Guid.NewGuid(),
            ProjectId = sharedGuids.ProjectId,
            SourceId = sharedGuids.SourceId,
            IngestBatchId = sharedGuids.BatchId,
            SourceRecordId = "fuzzy-noise",
            Fields = new Dictionary<string, string> { ["last_name"] = "Zzzzzz" },
            BlockingKeys = ["name:zzzzzz"],
            CreatedAt = DateTimeOffset.UtcNow
        };

        index.Index(indexed);
        index.Index(noise);
        index.Commit();

        var incoming = new EntityRecord
        {
            Id = Guid.NewGuid(),
            ProjectId = sharedGuids.ProjectId,
            SourceId = sharedGuids.SourceId,
            IngestBatchId = sharedGuids.BatchId,
            SourceRecordId = "fuzzy-incoming",
            Fields = new Dictionary<string, string> { ["last_name"] = "Andersen" },
            BlockingKeys = ["name:andersen"],
            CreatedAt = DateTimeOffset.UtcNow
        };

        var candidates = index.Retrieve(incoming, NoCorpus, Profile);

        // The fuzzy clause bridges "andersen" -> "anderson" (edit distance 1).
        Assert.Contains(candidates, c => c.Id == indexed.Id);
        // The noise record shares no blocking key and is not within edit distance of "andersen".
        Assert.DoesNotContain(candidates, c => c.Id == noise.Id);
    }

    [Fact]
    public void Retrieve_HonorsMaxCandidatesLimit()
    {
        var dir = LuceneTestRecords.TempDir();
        using var index = new LuceneCandidateRetrieval(new LuceneCandidateRetrievalOptions { IndexDirectory = dir, MaxCandidates = 3 });
        for (var i = 0; i < 10; i++)
            index.Index(LuceneTestRecords.Person($"e{i}", new Dictionary<string, string> { ["last_name"] = "Smith" }));
        index.Commit();

        var incoming = LuceneTestRecords.Person("c", new Dictionary<string, string> { ["last_name"] = "Smith" });
        var candidates = index.Retrieve(incoming, NoCorpus, Profile);

        Assert.True(candidates.Count <= 3, $"expected <= 3 candidates, got {candidates.Count}");
        Assert.Equal(3, candidates.Count);
    }

    [Fact]
    public void Constructor_CreatesFreshIndexDirectory_WhenItDoesNotExist()
    {
        // A path that does not yet exist on disk — the constructor must create it, not crash.
        var dir = Path.Combine(Path.GetTempPath(), "linkuity-lucene-tests", Guid.NewGuid().ToString("N"), "nested");
        Assert.False(Directory.Exists(dir));

        try
        {
            using (var index = new LuceneCandidateRetrieval(new LuceneCandidateRetrievalOptions { IndexDirectory = dir }))
            {
                Assert.True(Directory.Exists(dir));
            }
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Count_ReflectsIndexedDocuments()
    {
        var dir = LuceneTestRecords.TempDir();
        using var strategy = new LuceneCandidateRetrieval(new LuceneCandidateRetrievalOptions { IndexDirectory = dir });

        Assert.Equal(0, strategy.Count);

        strategy.Index(LuceneTestRecords.Person("a", new Dictionary<string, string> { ["email"] = "a@x.com" }));
        strategy.Index(LuceneTestRecords.Person("b", new Dictionary<string, string> { ["email"] = "b@x.com" }));
        strategy.Commit();

        Assert.Equal(2, strategy.Count);
    }
}
