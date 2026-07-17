using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;
using Linkuity.Pipeline;

namespace Linkuity.Matching.Tests;

public class UnionFindClusteringTests
{
    private static readonly IClusteringStrategy Strategy = new UnionFindClusteringStrategy();

    private static HashSet<string> Sorted(IEnumerable<string> ids) => [.. ids];

    [Fact]
    public void Cluster_GroupsConnectedComponents()
    {
        var ids = new[] { "a", "b", "c", "d" };
        var pairs = new[] { ("a", "b"), ("b", "c") };

        var clusters = Strategy.Cluster(ids, pairs);

        Assert.Equal(2, clusters.Count);
        Assert.Contains(clusters, c => Sorted(c).SetEquals(["a", "b", "c"]));
        Assert.Contains(clusters, c => Sorted(c).SetEquals(["d"]));
    }

    [Fact]
    public void Cluster_MatchesGraphServiceOutput()
    {
        var ids = new[] { "r1", "r2", "r3", "r4", "r5" };
        var pairs = new[] { ("r1", "r2"), ("r3", "r4"), ("r4", "r5") };

        var expected = new GraphService().FindClusters(ids, pairs)
            .Select(c => Sorted(c)).ToList();
        var actual = Strategy.Cluster(ids, pairs)
            .Select(c => Sorted(c)).ToList();

        Assert.Equal(expected.Count, actual.Count);
        foreach (var component in expected)
            Assert.Contains(actual, a => a.SetEquals(component));
    }
}
