using Linkuity.Core.Models;
using Linkuity.Mdm.Resolution;

namespace Linkuity.Mdm.Tests;

/// <summary>
/// In-memory reference implementation of <see cref="IResolutionContext"/> for the resolver unit
/// tests. Backed by plain lists of the durable state; the five port methods reproduce the bounded
/// reads <see cref="IncrementalResolver"/> performs (mirroring the FileMetadataStore LINQ filters).
/// </summary>
internal sealed class InMemoryResolutionContext : IResolutionContext
{
    public List<EntityRecord> Records { get; } = [];
    public List<Cluster> Clusters { get; } = [];
    public List<GoldenRecord> GoldenRecords { get; } = [];
    public List<GoldenRecordVersion> GoldenRecordVersions { get; } = [];

    public IReadOnlyList<EntityRecord> GetLinearCorpus(Guid projectId)
        => Records.Where(r => r.ProjectId == projectId).ToList();

    public IReadOnlyList<Cluster> GetActiveClustersContaining(Guid projectId, IReadOnlyCollection<Guid> recordIds)
    {
        var idSet = recordIds.ToHashSet();
        return Clusters
            .Where(c => c.ProjectId == projectId && c.Status != "merged"
                        && c.MemberEntityRecordIds.Any(idSet.Contains))
            .ToList();
    }

    public IReadOnlyList<EntityRecord> GetRecordsByIds(Guid projectId, IReadOnlyCollection<Guid> recordIds)
    {
        var idSet = recordIds.ToHashSet();
        return Records.Where(r => r.ProjectId == projectId && idSet.Contains(r.Id)).ToList();
    }

    public IReadOnlyList<GoldenRecord> GetGoldenRecordsForClusters(Guid projectId, IReadOnlyCollection<Guid> clusterIds)
    {
        var idSet = clusterIds.ToHashSet();
        return GoldenRecords.Where(g => g.ProjectId == projectId && idSet.Contains(g.ClusterId)).ToList();
    }

    public IReadOnlyList<GoldenRecordVersion> GetVersionsForGoldenRecords(IReadOnlyCollection<Guid> goldenRecordIds)
    {
        var idSet = goldenRecordIds.ToHashSet();
        return GoldenRecordVersions.Where(v => idSet.Contains(v.GoldenRecordId)).ToList();
    }
}
