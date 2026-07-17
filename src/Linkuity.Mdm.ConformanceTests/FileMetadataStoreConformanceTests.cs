using Linkuity.Core.Interfaces;
using Linkuity.Infrastructure.Local;
using Linkuity.Infrastructure.Lucene;
using Linkuity.Matching.Profiles;

namespace Linkuity.Mdm.ConformanceTests;

/// <summary>
/// Runs the full <see cref="MetadataStoreConformanceTests"/> suite against
/// <see cref="FileMetadataStore"/> backed by a per-test temp directory and a
/// <see cref="LuceneCandidateRetrieval"/> index. xUnit creates one instance per
/// [Fact], so <see cref="CreateStoreAsync"/> is called once per test; <see cref="Dispose"/>
/// disposes the Lucene index and deletes the temp directory after the test finishes.
/// </summary>
public sealed class FileMetadataStoreConformanceTests : MetadataStoreConformanceTests, IDisposable
{
    private string? _root;
    private LuceneCandidateRetrieval? _index;

    protected override Task<IMetadataStore> CreateStoreAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"linkuity-conf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        var dbPath = Path.Combine(_root, "metadata.json");
        var indexDir = Path.Combine(_root, "index");

        _index = new LuceneCandidateRetrieval(
            new LuceneCandidateRetrievalOptions { IndexDirectory = indexDir });

        var profileProvider = new DefaultMatchingProfileProvider(
            DefaultMatchingProfileProvider.BuiltInProfiles());

        IMetadataStore store = new FileMetadataStore(
            new FileMetadataStoreOptions { DatabasePath = dbPath },
            engine: null,
            profileProvider,
            _index);

        return Task.FromResult(store);
    }

    public void Dispose()
    {
        _index?.Dispose();

        if (_root is not null && Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* best-effort cleanup */ }
        }
    }
}
