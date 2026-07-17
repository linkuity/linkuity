using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;

namespace Linkuity.Infrastructure.Local.Tests;

public sealed class ProfileFallbackTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"linkuity-fallback-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task DefaultStore_ResolvesOrganizationProject_ZeroConfig()
    {
        // Person and organization profiles are behaviorally indistinguishable on organization-shaped data;
        // this test verifies the seeded profile is *active and functional* (it auto-matches via the
        // DomainName identifier), not merely non-throwing.
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = _path });
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Org", "organization", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "Import", now, CancellationToken.None);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now, CancellationToken.None);

        var record = new EntityRecord
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, SourceId = source.Id, IngestBatchId = batch.Id,
            SourceRecordId = "org-1",
            Fields = new Dictionary<string, string> { ["source"] = "CRM", ["organization_name"] = "Acme", ["domain_name"] = "acme.com" },
            BlockingKeys = [], CreatedAt = now
        };

        var result = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, batch.Id, [record], 0.90, 0.75), CancellationToken.None);
        Assert.Equal(1, result.RecordsAdded);

        // Second record: same domain_name → DomainName strong identifier floors score to 0.98 auto band → one cluster of two.
        var batch2 = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now, CancellationToken.None);
        var record2 = new EntityRecord
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, SourceId = source.Id, IngestBatchId = batch2.Id,
            SourceRecordId = "org-2",
            Fields = new Dictionary<string, string> { ["source"] = "ERP", ["organization_name"] = "Acme Corporation", ["domain_name"] = "acme.com" },
            BlockingKeys = [], CreatedAt = now
        };

        var result2 = await store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, batch2.Id, [record2], 0.90, 0.75), CancellationToken.None);
        Assert.Equal(1, result2.AutoMatches);

        var clusters = await store.ListClustersAsync(project.Id, CancellationToken.None);
        Assert.Single(clusters);
        Assert.Equal(2, clusters[0].MemberEntityRecordIds.Count);
    }

    [Fact]
    public async Task UnknownContentType_ThrowsHardError_NotSilentPersonFallback()
    {
        // Provider deliberately lacks 'organization' so ProfileFor must throw.
        var provider = new DefaultMatchingProfileProvider([DefaultMatchingProfileProvider.CreatePersonProfile()]);
        var store = new FileMetadataStore(new FileMetadataStoreOptions { DatabasePath = _path }, engine: null, provider, indexedRetrieval: null);
        var now = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("Org", "organization", now, CancellationToken.None);
        var source = await store.CreateSourceAsync(project.Id, "Import", now, CancellationToken.None);
        var batch = await store.CreateIngestBatchAsync(project.Id, source.Id, null, 1, now, CancellationToken.None);

        var record = new EntityRecord
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, SourceId = source.Id, IngestBatchId = batch.Id,
            SourceRecordId = "org-1",
            Fields = new Dictionary<string, string> { ["organization_name"] = "Acme" },
            BlockingKeys = [], CreatedAt = now
        };

        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() => store.SaveIncrementalIngestAsync(
            new IncrementalIngestRequest(project.Id, source.Id, batch.Id, [record], 0.90, 0.75), CancellationToken.None));
        Assert.Contains("organization", ex.Message);
        Assert.Contains("person", ex.Message);
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
