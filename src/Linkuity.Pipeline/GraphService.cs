namespace Linkuity.Pipeline;

public class GraphService
{
    public IReadOnlyList<IReadOnlyList<string>> FindClusters(
        IEnumerable<string> allIds,
        IEnumerable<(string Left, string Right)> pairs)
    {
        var parent = new Dictionary<string, string>();
        var rank = new Dictionary<string, int>();

        foreach (var id in allIds)
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

    private static void Union(
        Dictionary<string, string> parent,
        Dictionary<string, int> rank,
        string a,
        string b)
    {
        var rootA = Find(parent, a);
        var rootB = Find(parent, b);
        if (rootA == rootB) return;
        if (rank[rootA] < rank[rootB]) (rootA, rootB) = (rootB, rootA);
        parent[rootB] = rootA;
        if (rank[rootA] == rank[rootB]) rank[rootA]++;
    }
}
