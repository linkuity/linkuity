using Linkuity.Core.Models;
using Linkuity.Infrastructure.Lucene;
using Linkuity.Matching.Profiles;

namespace Linkuity.Infrastructure.Lucene.Tests;

public class LuceneSuppressionTests
{
    private static readonly IReadOnlyCollection<EntityRecord> NoCorpus = [];

    private static MatchingProfile Profile(int? maxBlockSize)
    {
        var p = DefaultMatchingProfileProvider.CreatePersonProfile();
        return new MatchingProfile
        {
            ContentType = p.ContentType, Fields = p.Fields,
            NormalizationStrategy = p.NormalizationStrategy, BlockingStrategies = p.BlockingStrategies,
            CandidateRetrievalStrategy = p.CandidateRetrievalStrategy, SimilarityStrategy = p.SimilarityStrategy,
            ScoringStrategy = p.ScoringStrategy, DecisionStrategy = p.DecisionStrategy,
            ClusteringStrategy = p.ClusteringStrategy, AutoMatchThreshold = p.AutoMatchThreshold,
            ReviewThreshold = p.ReviewThreshold, ReviewFloorGate = p.ReviewFloorGate,
            MaxBlockSize = maxBlockSize
        };
    }

    private static LuceneCandidateRetrieval NewIndex(int maxCandidates = 50)
        => new(new LuceneCandidateRetrievalOptions { IndexDirectory = LuceneTestRecords.TempDir(), MaxCandidates = maxCandidates });

    private static EntityRecord SharedEmail(string id)
        => LuceneTestRecords.Person(id, new Dictionary<string, string> { ["email"] = "shared@example.com" });

    [Fact]
    public void ProfileThreshold_SuppressesGenericKey_RetrievalEmpty()
    {
        using var index = NewIndex();
        index.Index(SharedEmail("a"));
        index.Index(SharedEmail("b"));
        index.Index(SharedEmail("c"));
        index.Commit();

        // email key DocFreq = 3 > maxBlockSize 2 -> suppressed -> no clauses -> empty.
        Assert.Empty(index.Retrieve(SharedEmail("in"), NoCorpus, Profile(2)));
    }

    [Fact]
    public void ProfileThreshold_AtBoundary_StaysActive()
    {
        using var index = NewIndex();
        index.Index(SharedEmail("a"));
        index.Index(SharedEmail("b"));
        index.Index(SharedEmail("c"));
        index.Commit();

        Assert.Equal(3, index.Retrieve(SharedEmail("in"), NoCorpus, Profile(3)).Count);
    }

    [Fact]
    public void SuppressedKey_OtherKeysStillRetrieve()
    {
        using var index = NewIndex();
        index.Index(SharedEmail("a"));
        index.Index(SharedEmail("b"));
        index.Index(SharedEmail("c"));
        var rare = LuceneTestRecords.Person("rare", new Dictionary<string, string> { ["last_name"] = "Zabriskie" });
        index.Index(rare);
        index.Commit();

        var incoming = LuceneTestRecords.Person("in", new Dictionary<string, string>
        {
            ["email"] = "shared@example.com", ["last_name"] = "Zabriskie"
        });
        var candidates = index.Retrieve(incoming, NoCorpus, Profile(2));

        Assert.Contains(candidates, c => c.Id == rare.Id);
        Assert.DoesNotContain(candidates, c => c.SourceRecordId == "a"); // email-only overlap suppressed
    }

    [Fact]
    public void SuppressedNameKey_AlsoDropsItsFuzzyClause()
    {
        using var index = NewIndex();
        // Three Smiths make name:smith (and its phonetic) generic at threshold 2.
        index.Index(LuceneTestRecords.Person("s1", new Dictionary<string, string> { ["last_name"] = "Smith" }));
        index.Index(LuceneTestRecords.Person("s2", new Dictionary<string, string> { ["last_name"] = "Smith" }));
        index.Index(LuceneTestRecords.Person("s3", new Dictionary<string, string> { ["last_name"] = "Smith" }));
        index.Commit();

        // Incoming Smyth shares no exact key with Smith; pre-2b the fuzzy expansion of its
        // name key could still reach Smith docs. With name:smyth active (freq 0) that fuzzy
        // clause may match Smith's indexed name token — the assertion here pins the SUPPRESSED
        // side: an incoming Smith (suppressed name key) must not reach anything via the fuzzy
        // clause derived from that suppressed key.
        var incoming = LuceneTestRecords.Person("in", new Dictionary<string, string> { ["last_name"] = "Smith" });
        Assert.Empty(index.Retrieve(incoming, NoCorpus, Profile(2)));
    }

    [Fact]
    public void NoProfileThreshold_DefaultsToMaxCandidates()
    {
        using var small = NewIndex(maxCandidates: 2);
        small.Index(SharedEmail("a"));
        small.Index(SharedEmail("b"));
        small.Index(SharedEmail("c"));
        small.Commit();

        // DocFreq 3 > MaxCandidates 2 -> suppressed by the derived default -> empty
        // (previously: silently truncated to an arbitrary Top-2).
        Assert.Empty(small.Retrieve(SharedEmail("in"), NoCorpus, Profile(null)));
    }

    [Fact]
    public void NoProfileThreshold_UnderMaxCandidates_Unaffected()
    {
        using var index = NewIndex(maxCandidates: 50);
        index.Index(SharedEmail("a"));
        index.Index(SharedEmail("b"));
        index.Commit();

        Assert.Equal(2, index.Retrieve(SharedEmail("in"), NoCorpus, Profile(null)).Count);
    }
}
