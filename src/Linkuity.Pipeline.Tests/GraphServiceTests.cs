using Linkuity.Pipeline;

namespace Linkuity.Pipeline.Tests;

public class GraphServiceTests
{
    private readonly GraphService _svc = new();

    [Fact]
    public void FindClusters_TransitiveChain_ReturnsOneCluster()
    {
        var allIds = new[] { "A", "B", "C" };
        var pairs = new (string, string)[] { ("A", "B"), ("B", "C") };

        var clusters = _svc.FindClusters(allIds, pairs);

        Assert.Single(clusters);
        Assert.Equal(3, clusters[0].Count);
        Assert.Contains("A", clusters[0]);
        Assert.Contains("B", clusters[0]);
        Assert.Contains("C", clusters[0]);
    }

    [Fact]
    public void FindClusters_TwoIndependentPairs_ReturnsTwoClusters()
    {
        var allIds = new[] { "A", "B", "C", "D" };
        var pairs = new (string, string)[] { ("A", "B"), ("C", "D") };

        var clusters = _svc.FindClusters(allIds, pairs);

        Assert.Equal(2, clusters.Count);
        var idSets = clusters.Select(c => c.ToHashSet()).ToList();
        Assert.Contains(idSets, s => s.SetEquals(new[] { "A", "B" }));
        Assert.Contains(idSets, s => s.SetEquals(new[] { "C", "D" }));
    }

    [Fact]
    public void FindClusters_NoPairs_EachRecordIsOwnCluster()
    {
        var allIds = new[] { "A", "B", "C" };

        var clusters = _svc.FindClusters(allIds, Array.Empty<(string, string)>());

        Assert.Equal(3, clusters.Count);
        Assert.All(clusters, c => Assert.Single(c));
    }

    [Fact]
    public void FindClusters_DuplicatePair_Idempotent()
    {
        var allIds = new[] { "A", "B" };
        var pairs = new (string, string)[] { ("A", "B"), ("A", "B") };

        var clusters = _svc.FindClusters(allIds, pairs);

        Assert.Single(clusters);
        Assert.Equal(2, clusters[0].Count);
    }

    [Fact]
    public void FindClusters_MixedMatchedAndSingleton_SingletonIncluded()
    {
        var allIds = new[] { "A", "B", "C" };
        var pairs = new (string, string)[] { ("A", "B") };

        var clusters = _svc.FindClusters(allIds, pairs);

        Assert.Equal(2, clusters.Count);
        var idSets = clusters.Select(c => c.ToHashSet()).ToList();
        Assert.Contains(idSets, s => s.SetEquals(new[] { "A", "B" }));
        Assert.Contains(idSets, s => s.SetEquals(new[] { "C" }));
    }
}
