namespace Linkuity.Matching.Strategies.Defaults;

/// <summary>
/// Union-Find connected-components clustering with path compression and union by
/// rank, ported from GraphService.FindClusters. Implemented and unit-tested here;
/// wiring it into the durable path is Milestone 16.
/// </summary>
public sealed class UnionFindClusteringStrategy : IClusteringStrategy
{
    public string Name => "union-find";

    public IReadOnlyList<IReadOnlyList<string>> Cluster(IEnumerable<string> ids, IEnumerable<(string Left, string Right)> pairs)
    {
        var parent = new Dictionary<string, string>();
        var rank = new Dictionary<string, int>();

        foreach (var id in ids)
        {
            parent[id] = id;
            rank[id] = 0;
        }

        foreach (var (left, right) in pairs)
        {
            if (!parent.ContainsKey(left)) { parent[left] = left; rank[left] = 0; }
            if (!parent.ContainsKey(right)) { parent[right] = right; rank[right] = 0; }
            Union(parent, rank, left, right);
        }

        return parent.Keys
            .GroupBy(id => Find(parent, id))
            .Select(g => (IReadOnlyList<string>)g.ToList())
            .ToList();
    }

    private static string Find(Dictionary<string, string> parent, string id)
    {
        var root = id;
        while (parent[root] != root) root = parent[root];
        while (parent[id] != root)
        {
            var next = parent[id];
            parent[id] = root;
            id = next;
        }
        return root;
    }

    private static void Union(Dictionary<string, string> parent, Dictionary<string, int> rank, string a, string b)
    {
        var rootA = Find(parent, a);
        var rootB = Find(parent, b);
        if (rootA == rootB) return;
        if (rank[rootA] < rank[rootB]) (rootA, rootB) = (rootB, rootA);
        parent[rootB] = rootA;
        if (rank[rootA] == rank[rootB]) rank[rootA]++;
    }
}
