namespace Linkuity.Pipeline.Tests;

/// <summary>
/// The shape test at the centre of the cluster measurement: a chained cluster is held together
/// by single edges, a genuine duplicate group is not.
/// </summary>
public class EdgeGraphTests
{
    [Fact]
    public void Triangle_HasNoBridges()
    {
        (int, int)[] edges = [(0, 1), (1, 2), (0, 2)];

        var bridges = EdgeGraph.FindBridges(3, edges);

        Assert.All(bridges, b => Assert.False(b));
    }

    [Fact]
    public void Chain_EveryEdgeIsABridge()
    {
        (int, int)[] edges = [(0, 1), (1, 2), (2, 3)];

        var bridges = EdgeGraph.FindBridges(4, edges);

        Assert.All(bridges, Assert.True);
    }

    [Fact]
    public void TwoTrianglesJoinedByOneEdge_OnlyTheJoinIsABridge()
    {
        // The Example 2 shape: two well-formed groups merged by a single wrong decision.
        (int, int)[] edges =
        [
            (0, 1), (1, 2), (0, 2),      // group A
            (3, 4), (4, 5), (3, 5),      // group B
            (2, 3)                       // the one bad link
        ];

        var bridges = EdgeGraph.FindBridges(6, edges);

        Assert.Equal(1, bridges.Count(b => b));
        Assert.True(bridges[6]);
    }

    [Fact]
    public void TwoEdgeConnectedComponents_SeparateTheGroupsAcrossABridge()
    {
        (int, int)[] edges = [(0, 1), (1, 2), (0, 2), (3, 4), (4, 5), (3, 5), (2, 3)];
        var bridges = EdgeGraph.FindBridges(6, edges);

        var roots = EdgeGraph.TwoEdgeConnectedComponents(6, edges, bridges);

        Assert.Equal(roots[0], roots[1]);
        Assert.Equal(roots[0], roots[2]);
        Assert.Equal(roots[3], roots[4]);
        Assert.Equal(roots[3], roots[5]);
        Assert.NotEqual(roots[0], roots[3]);
    }

    [Fact]
    public void ChainCollapsesToSingletons_WhileACliqueSurvivesIntact()
    {
        (int, int)[] chain = [(0, 1), (1, 2), (2, 3)];
        var chainRoots = EdgeGraph.TwoEdgeConnectedComponents(
            4, chain, EdgeGraph.FindBridges(4, chain));
        Assert.Equal(4, chainRoots.Distinct().Count());

        (int, int)[] clique = [(0, 1), (0, 2), (0, 3), (1, 2), (1, 3), (2, 3)];
        var cliqueRoots = EdgeGraph.TwoEdgeConnectedComponents(
            4, clique, EdgeGraph.FindBridges(4, clique));
        Assert.Single(cliqueRoots.Distinct());
    }

    [Fact]
    public void ParallelEdgesBetweenTheSamePair_AreNotBridges()
    {
        // Guards the edge-id skip: skipping by PARENT NODE instead would call both of these
        // bridges, because the second edge would look like a re-entry rather than a second route.
        (int, int)[] edges = [(0, 1), (0, 1)];

        var bridges = EdgeGraph.FindBridges(2, edges);

        Assert.All(bridges, b => Assert.False(b));
    }

    [Fact]
    public void DisconnectedComponents_AreAnalyzedIndependently()
    {
        (int, int)[] edges = [(0, 1), (2, 3), (3, 4), (2, 4)];

        var bridges = EdgeGraph.FindBridges(5, edges);

        Assert.True(bridges[0]);                       // the isolated pair
        Assert.All(bridges.Skip(1), b => Assert.False(b));  // the triangle
    }

    [Fact]
    public void IsolatedNodeWithNoEdges_DoesNotDisturbTheWalk()
    {
        (int, int)[] edges = [(0, 1), (0, 2), (1, 2)];

        var bridges = EdgeGraph.FindBridges(5, edges);   // nodes 3 and 4 have no edges

        Assert.All(bridges, b => Assert.False(b));
    }

    [Fact]
    public void ThirtyThousandNodeChain_CompletesWithoutStackOverflow()
    {
        // The measurement exists because of a 29,477-record cluster. A recursive depth-first
        // search over a chain that long overflows the stack before reporting anything, so the
        // iterative implementation is a requirement rather than a preference.
        const int n = 30_000;
        var edges = new (int, int)[n - 1];
        for (var i = 0; i < n - 1; i++) edges[i] = (i, i + 1);

        var bridges = EdgeGraph.FindBridges(n, edges);

        Assert.Equal(n - 1, bridges.Count(b => b));
    }
}
