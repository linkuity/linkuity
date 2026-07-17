namespace Linkuity.Matching.Strategies;

public interface IClusteringStrategy
{
    string Name { get; }
    IReadOnlyList<IReadOnlyList<string>> Cluster(IEnumerable<string> ids, IEnumerable<(string Left, string Right)> pairs);
}
