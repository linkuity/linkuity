using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;

namespace Linkuity.Matching.Tests;

public class StrategyRegistryTests
{
    private sealed class FakeNorm : INormalizationStrategy
    {
        public string Name => "n";
        public EntityRecord Normalize(EntityRecord record, MatchingProfile profile) => record;
    }
    private sealed class FakeBlock : IBlockingStrategy
    {
        public string Name => "b";
        public IReadOnlyList<string> GenerateKeys(EntityRecord record, MatchingProfile profile) => [];
    }
    private sealed class FakeRetrieve : ICandidateRetrievalStrategy
    {
        public string Name => "r";
        public IReadOnlyList<EntityRecord> Retrieve(EntityRecord record, IReadOnlyCollection<EntityRecord> corpus, MatchingProfile profile) => [];
    }
    private sealed class FakeSim : ISimilarityStrategy
    {
        public string Name => "s";
        public IReadOnlyList<SimilaritySignal> Evaluate(EntityRecord left, EntityRecord right, MatchingProfile profile) => [];
    }
    private sealed class FakeScore : IScoringStrategy
    {
        public string Name => "sc";
        public ScoreResult Score(IReadOnlyList<SimilaritySignal> signals, MatchingProfile profile) => new(0, []);
    }
    private sealed class FakeDecide : IDecisionStrategy
    {
        public string Name => "d";
        public MatchDecision Decide(double topScore, MatchingProfile profile) => MatchDecision.NoMatch;
    }
    private sealed class FakeCluster : IClusteringStrategy
    {
        public string Name => "c";
        public IReadOnlyList<IReadOnlyList<string>> Cluster(IEnumerable<string> ids, IEnumerable<(string Left, string Right)> pairs) => [];
    }
    private sealed class FakeEvaluator : ISimilarityEvaluator
    {
        public string Name => "e";
        public double? Evaluate(string left, string right, Linkuity.Matching.Profiles.ProfileField field) => 1.0;
    }

    [Fact]
    public void Registry_ExposesEachCategoryKeyedByName()
    {
        var registry = new DefaultStrategyRegistry(
            [new FakeNorm()], [new FakeBlock()], [new FakeRetrieve()], [new FakeSim()],
            [new FakeScore()], [new FakeDecide()], [new FakeCluster()], [new FakeEvaluator()]);

        Assert.Equal("n", registry.Normalization["n"].Name);
        Assert.Equal("b", registry.Blocking["b"].Name);
        Assert.Equal("r", registry.CandidateRetrieval["r"].Name);
        Assert.Equal("s", registry.Similarity["s"].Name);
        Assert.Equal("sc", registry.Scoring["sc"].Name);
        Assert.Equal("d", registry.Decision["d"].Name);
        Assert.Equal("c", registry.Clustering["c"].Name);
        Assert.Equal("e", registry.Evaluators["e"].Name);
    }

    [Fact]
    public void Registry_RejectsDuplicateNamesWithinACategory()
    {
        Assert.Throws<ArgumentException>(() => new DefaultStrategyRegistry(
            [new FakeNorm(), new FakeNorm()], [new FakeBlock()], [new FakeRetrieve()], [new FakeSim()],
            [new FakeScore()], [new FakeDecide()], [new FakeCluster()], [new FakeEvaluator()]));
    }
}
