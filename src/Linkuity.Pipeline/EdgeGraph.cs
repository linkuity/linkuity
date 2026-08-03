namespace Linkuity.Pipeline;

/// <summary>
/// Undirected-graph shape analysis over the auto-merge edges inside a cluster.
/// <para>
/// The question this exists to answer: can a cluster be split in two by removing a single
/// merge decision? An edge whose removal disconnects the graph is a BRIDGE, and a cluster
/// built by chaining is made almost entirely of them. A cluster of records that genuinely
/// all match each other has none.
/// </para>
/// <para>
/// Both passes are ITERATIVE, not recursive, and deliberately so: the failure that motivated
/// this measurement is a single cluster of 29,477 records, and a recursive depth-first search
/// over a chain that long overflows the stack before it reports anything.
/// </para>
/// </summary>
internal static class EdgeGraph
{
    /// <summary>Compressed adjacency: for node u, neighbours live in [Offset[u], Offset[u+1]).</summary>
    internal readonly record struct Csr(int[] Offset, int[] Neighbour, int[] EdgeId);

    internal static Csr Build(int nodeCount, IReadOnlyList<(int Left, int Right)> edges)
    {
        var offset = new int[nodeCount + 1];
        foreach (var (l, r) in edges) { offset[l + 1]++; offset[r + 1]++; }
        for (var i = 0; i < nodeCount; i++) offset[i + 1] += offset[i];

        var cursor = new int[nodeCount];
        Array.Copy(offset, cursor, nodeCount);
        var neighbour = new int[edges.Count * 2];
        var edgeId = new int[edges.Count * 2];
        for (var e = 0; e < edges.Count; e++)
        {
            var (l, r) = edges[e];
            neighbour[cursor[l]] = r; edgeId[cursor[l]++] = e;
            neighbour[cursor[r]] = l; edgeId[cursor[r]++] = e;
        }
        return new Csr(offset, neighbour, edgeId);
    }

    /// <summary>
    /// Marks every bridge. Iterative Tarjan: an edge (u,v) explored from u is a bridge when no
    /// back-edge from v's subtree reaches u or above. The edge id — not the parent node — is what
    /// gets skipped, so a duplicated pair between the same two records cannot be mistaken for a
    /// second independent connection.
    /// </summary>
    internal static bool[] FindBridges(int nodeCount, IReadOnlyList<(int Left, int Right)> edges)
    {
        var csr = Build(nodeCount, edges);
        var isBridge = new bool[edges.Count];
        var disc = new int[nodeCount];      // 0 = unvisited; discovery order starts at 1
        var low = new int[nodeCount];
        var timer = 0;

        var nodeStack = new int[nodeCount];
        var iterStack = new int[nodeCount];
        var parentEdge = new int[nodeCount];

        for (var start = 0; start < nodeCount; start++)
        {
            if (disc[start] != 0) continue;
            var top = 0;
            disc[start] = low[start] = ++timer;
            nodeStack[0] = start; iterStack[0] = csr.Offset[start]; parentEdge[0] = -1;

            while (top >= 0)
            {
                var u = nodeStack[top];
                if (iterStack[top] < csr.Offset[u + 1])
                {
                    var i = iterStack[top]++;
                    var e = csr.EdgeId[i];
                    if (e == parentEdge[top]) continue;

                    var v = csr.Neighbour[i];
                    if (disc[v] != 0)
                    {
                        if (disc[v] < low[u]) low[u] = disc[v];
                    }
                    else
                    {
                        disc[v] = low[v] = ++timer;
                        top++;
                        nodeStack[top] = v; iterStack[top] = csr.Offset[v]; parentEdge[top] = e;
                    }
                }
                else
                {
                    var childEdge = parentEdge[top];
                    top--;
                    if (top < 0) break;
                    var parent = nodeStack[top];
                    if (low[u] < low[parent]) low[parent] = low[u];
                    if (low[u] > disc[parent]) isBridge[childEdge] = true;
                }
            }
        }
        return isBridge;
    }

    /// <summary>
    /// Groups nodes into two-edge-connected components: what survives when every bridge is cut.
    /// A chain collapses to singletons; a group of records that all match each other stays whole.
    /// Returns a representative node id per node (union-find root), so component sizes are a tally.
    /// </summary>
    internal static int[] TwoEdgeConnectedComponents(
        int nodeCount, IReadOnlyList<(int Left, int Right)> edges, bool[] isBridge)
    {
        var uf = new CorpusAuditService.UnionFind(nodeCount);
        for (var e = 0; e < edges.Count; e++)
            if (!isBridge[e]) uf.Union(edges[e].Left, edges[e].Right);

        var root = new int[nodeCount];
        for (var i = 0; i < nodeCount; i++) root[i] = uf.Find(i);
        return root;
    }
}
