using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Matching.Tests;

public class BlockingLinearSuppressionTests
{
    private static readonly BlockingAwareLinearRetrievalStrategy Strategy = new();

    private static EntityRecord Rec(string id, params string[] keys)
        => TestRecords.Person(id, new Dictionary<string, string>(), keys);

    private static MatchingProfile Profile(int? maxBlockSize) => new()
    {
        ContentType = TestProfiles.Person.ContentType,
        Fields = TestProfiles.Person.Fields,
        NormalizationStrategy = TestProfiles.Person.NormalizationStrategy,
        BlockingStrategies = TestProfiles.Person.BlockingStrategies,
        CandidateRetrievalStrategy = "blocking-linear",
        SimilarityStrategy = TestProfiles.Person.SimilarityStrategy,
        ScoringStrategy = TestProfiles.Person.ScoringStrategy,
        DecisionStrategy = TestProfiles.Person.DecisionStrategy,
        ClusteringStrategy = TestProfiles.Person.ClusteringStrategy,
        AutoMatchThreshold = TestProfiles.Person.AutoMatchThreshold,
        ReviewThreshold = TestProfiles.Person.ReviewThreshold,
        MaxBlockSize = maxBlockSize
    };

    // Corpus: three records in the junk "name:inc" block, one sharing the rare "fp:acme".
    private static IReadOnlyCollection<EntityRecord> Corpus() =>
    [
        Rec("junk1", "name:inc"),
        Rec("junk2", "name:inc"),
        Rec("junk3", "name:inc"),
        Rec("rare", "fp:acme")
    ];

    [Fact]
    public void Unset_RetrievesViaJunkKey_TodaysBehaviorPinned()
    {
        var candidates = Strategy.Retrieve(Rec("in", "name:inc", "fp:acme"), Corpus(), Profile(null));
        Assert.Equal(4, candidates.Count); // all three junk-block members AND the rare match
    }

    [Fact]
    public void Set_SuppressedKeyStopsDrivingCandidacy_OtherKeysUnaffected()
    {
        // name:inc block size 3 > 2 -> suppressed; fp:acme block size 1 -> active.
        var candidates = Strategy.Retrieve(Rec("in", "name:inc", "fp:acme"), Corpus(), Profile(2));
        var only = Assert.Single(candidates);
        Assert.Equal("rare", only.SourceRecordId);
    }

    [Fact]
    public void Set_BlockExactlyAtThreshold_StaysActive()
    {
        var candidates = Strategy.Retrieve(Rec("in", "name:inc"), Corpus(), Profile(3));
        Assert.Equal(3, candidates.Count); // size 3 == max 3 -> active
    }

    [Fact]
    public void Set_AllKeysSuppressed_RetrievesNothing()
    {
        var candidates = Strategy.Retrieve(Rec("in", "name:inc"), Corpus(), Profile(2));
        Assert.Empty(candidates); // blocking singleton
    }

    [Fact]
    public void Set_NoKeys_StillRetrievesNothing()
        => Assert.Empty(Strategy.Retrieve(Rec("in"), Corpus(), Profile(2)));
}
