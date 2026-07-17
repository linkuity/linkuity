using System.Text.Json;
using System.Collections.Concurrent;
using Linkuity.Core.Interfaces;
using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;
using Linkuity.Mdm.Resolution;

namespace Linkuity.Infrastructure.Local;

public sealed class FileMetadataStore : IMetadataStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _databasePath;
    private readonly SemaphoreSlim _gate;
    private readonly IMatchingEngine _engine;
    private readonly IMatchingProfileProvider _profileProvider;
    private readonly IIndexedCandidateRetrievalStrategy? _index;
    private readonly IncrementalResolver _resolver;

    public FileMetadataStore(FileMetadataStoreOptions options)
        : this(options, engine: null, profileProvider: null, indexedRetrieval: null)
    {
    }

    public FileMetadataStore(
        FileMetadataStoreOptions options,
        IMatchingEngine? engine,
        IMatchingProfileProvider? profileProvider,
        IIndexedCandidateRetrievalStrategy? indexedRetrieval)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.DatabasePath))
            throw new ArgumentException("Metadata database path is required.", nameof(options));

        _databasePath = Path.GetFullPath(options.DatabasePath);
        _gate = Gates.GetOrAdd(_databasePath, _ => new SemaphoreSlim(1, 1));
        _index = indexedRetrieval;
        _engine = indexedRetrieval is not null
            ? MatchingDefaults.CreateEngine(indexedRetrieval)
            : (engine ?? MatchingDefaults.CreateEngine());
        _profileProvider = profileProvider
            ?? new DefaultMatchingProfileProvider(DefaultMatchingProfileProvider.BuiltInProfiles());
        _resolver = new IncrementalResolver(_engine, indexedRetrieval is not null);
    }

    public Task<Project> CreateProjectAsync(string name, string contentType, DateTimeOffset createdAt, CancellationToken ct = default)
        => CreateProjectAsync(name, contentType, null, createdAt, ct);

    public async Task<Project> CreateProjectAsync(string name, string contentType, MergeConfiguration? mergeConfiguration, DateTimeOffset createdAt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Project name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(contentType))
            throw new ArgumentException("Project content type is required.", nameof(contentType));
        ValidateMergeConfiguration(mergeConfiguration);

        await _gate.WaitAsync(ct);
        try
        {
            var db = await LoadAsync(ct);
            if (db.Projects.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Project already exists: {name}");

            var project = new Project
            {
                Id = Guid.NewGuid(),
                Name = name,
                ContentType = contentType,
                MergeConfiguration = mergeConfiguration,
                CreatedAt = createdAt
            };
            db.Projects.Add(project);
            await SaveAsync(db, ct);
            return project;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct = default)
        => (await LoadAsync(ct)).Projects.OrderBy(p => p.CreatedAt).ToList();

    public async Task<Project?> GetProjectAsync(Guid projectId, CancellationToken ct = default)
        => (await LoadAsync(ct)).Projects.FirstOrDefault(p => p.Id == projectId);

    public async Task<Project> UpdateProjectMergePolicyAsync(Guid projectId, MergeConfiguration? mergeConfiguration, CancellationToken ct = default)
    {
        ValidateMergeConfiguration(mergeConfiguration);
        await _gate.WaitAsync(ct);
        try
        {
            var db = await LoadAsync(ct);
            var project = db.Projects.FirstOrDefault(p => p.Id == projectId)
                ?? throw new InvalidOperationException($"Project not found: {projectId}");
            var updated = new Project
            {
                Id = project.Id,
                Name = project.Name,
                ContentType = project.ContentType,
                MergeConfiguration = mergeConfiguration,
                CreatedAt = project.CreatedAt
            };
            db.Projects.RemoveAll(p => p.Id == projectId);
            db.Projects.Add(updated);
            await SaveAsync(db, ct);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Source> CreateSourceAsync(Guid projectId, string name, DateTimeOffset createdAt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Source name is required.", nameof(name));

        await _gate.WaitAsync(ct);
        try
        {
            var db = await LoadAsync(ct);
            if (!db.Projects.Any(p => p.Id == projectId))
                throw new InvalidOperationException($"Project not found: {projectId}");
            if (db.Sources.Any(s => s.ProjectId == projectId && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Source already exists for project {projectId}: {name}");

            var source = new Source { Id = Guid.NewGuid(), ProjectId = projectId, Name = name, CreatedAt = createdAt };
            db.Sources.Add(source);
            await SaveAsync(db, ct);
            return source;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<Source>> ListSourcesAsync(Guid projectId, CancellationToken ct = default)
        => (await LoadAsync(ct)).Sources.Where(s => s.ProjectId == projectId).OrderBy(s => s.CreatedAt).ToList();

    public async Task<Source?> GetSourceAsync(Guid sourceId, CancellationToken ct = default)
        => (await LoadAsync(ct)).Sources.FirstOrDefault(s => s.Id == sourceId);

    public async Task<IngestBatch> CreateIngestBatchAsync(Guid projectId, Guid sourceId, Guid? jobId, int recordCount, DateTimeOffset createdAt, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var db = await LoadAsync(ct);
            if (!db.Projects.Any(p => p.Id == projectId))
                throw new InvalidOperationException($"Project not found: {projectId}");
            if (!db.Sources.Any(s => s.Id == sourceId && s.ProjectId == projectId))
                throw new InvalidOperationException($"Source not found for project {projectId}: {sourceId}");

            var batch = new IngestBatch
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                SourceId = sourceId,
                JobId = jobId,
                RecordCount = recordCount,
                CreatedAt = createdAt
            };
            db.IngestBatches.Add(batch);
            await SaveAsync(db, ct);
            return batch;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<IngestBatch>> ListIngestBatchesAsync(Guid projectId, CancellationToken ct = default)
        => (await LoadAsync(ct)).IngestBatches.Where(b => b.ProjectId == projectId).OrderBy(b => b.CreatedAt).ToList();

    public async Task SaveCompletedBatchAsync(CompletedBatchMetadata completedBatch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(completedBatch);
        await _gate.WaitAsync(ct);
        try
        {
            var db = await LoadAsync(ct);
            var completedBatchWithKeys = WithGeneratedBlockingKeys(db, completedBatch);
            ValidateCompletedBatch(db, completedBatchWithKeys);
            var mutations = CompletedBatchResolver.Resolve(completedBatchWithKeys, db.Projects, DateTimeOffset.UtcNow);
            ApplyMutations(db, mutations);
            IndexRecords(completedBatchWithKeys.EntityRecords);
            await SaveAsync(db, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IncrementalIngestResult> SaveIncrementalIngestAsync(IncrementalIngestRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AutoMatchThreshold <= request.ReviewThreshold)
            throw new ArgumentException("Auto-match threshold must be greater than review threshold.", nameof(request));
        if (request.ReviewThreshold < 0 || request.AutoMatchThreshold > 1)
            throw new ArgumentException("Thresholds must be between 0 and 1.", nameof(request));

        await _gate.WaitAsync(ct);
        try
        {
            var db = await LoadAsync(ct);
            ValidateIncrementalRequest(db, request);
            var project = db.Projects.First(p => p.Id == request.ProjectId);
            BackfillBlockingKeys(db, request.ProjectId);

            var profile = ProfileFor(project.ContentType);
            if (_index is not null)
                EnsureIndexCurrent(db);

            var incomingRecords = request.Records
                .Select(record => record.BlockingKeys.Count > 0
                    ? record
                    : WithBlockingKeys(record, GenerateEngineBlockingKeys(record, profile)))
                .ToList();

            var (result, mutations) = _resolver.Resolve(
                request, project, profile, incomingRecords, new FileResolutionContext(db), DateTimeOffset.UtcNow);

            ApplyMutations(db, mutations);
            UpdateBatchRecordCount(db, request.IngestBatchId, request.Records.Count);
            IndexRecords(incomingRecords);
            await SaveAsync(db, ct);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Reconcile the batch's stored RecordCount with the rows actually ingested in this call.
    // IngestBatch is an init-only class, so rebuild the working-set entry in place.
    private static void UpdateBatchRecordCount(ResolutionWorkingSet db, Guid batchId, int count)
    {
        var index = db.IngestBatches.FindIndex(b => b.Id == batchId);
        if (index < 0)
            return;

        var batch = db.IngestBatches[index];
        db.IngestBatches[index] = new IngestBatch
        {
            Id = batch.Id,
            ProjectId = batch.ProjectId,
            SourceId = batch.SourceId,
            JobId = batch.JobId,
            RecordCount = count,
            CreatedAt = batch.CreatedAt
        };
    }

    private static void ApplyMutations(ResolutionWorkingSet db, MutationSet m)
    {
        db.EntityRecords.AddRange(m.RecordsToInsert);

        foreach (var c in m.ClustersToUpsert)
        {
            db.Clusters.RemoveAll(x => x.Id == c.Id);
            db.Clusters.Add(c);
        }

        db.GoldenRecords.RemoveAll(g => m.GoldenRecordClusterIdsToClear.Contains(g.ClusterId));
        foreach (var g in m.GoldenRecordsToUpsert)
        {
            db.GoldenRecords.RemoveAll(x => x.Id == g.Id);
            db.GoldenRecords.Add(g);
        }

        db.GoldenRecordVersions.AddRange(m.VersionsToInsert);
        db.MatchEdges.AddRange(m.EdgesToInsert);
        db.ReviewTasks.AddRange(m.ReviewTasksToInsert);
        db.ClusterMergeEvents.AddRange(m.MergeEventsToInsert);
    }

    public async Task<IReadOnlyList<EntityRecord>> ListEntityRecordsAsync(Guid projectId, CancellationToken ct = default)
        => (await LoadAsync(ct)).EntityRecords.Where(r => r.ProjectId == projectId).ToList();

    public async Task<IReadOnlyList<MatchEdge>> ListMatchEdgesAsync(Guid projectId, CancellationToken ct = default)
        => (await LoadAsync(ct)).MatchEdges.Where(e => e.ProjectId == projectId).ToList();

    public async Task<IReadOnlyList<Cluster>> ListClustersAsync(Guid projectId, CancellationToken ct = default)
        => (await LoadAsync(ct)).Clusters.Where(c => c.ProjectId == projectId && c.Status != "merged").ToList();

    public async Task<IReadOnlyList<GoldenRecord>> ListGoldenRecordsAsync(Guid projectId, CancellationToken ct = default)
    {
        var db = await LoadAsync(ct);
        var activeClusterIds = db.Clusters
            .Where(c => c.ProjectId == projectId && c.Status != "merged")
            .Select(c => c.Id)
            .ToHashSet();
        return db.GoldenRecords.Where(g => g.ProjectId == projectId && activeClusterIds.Contains(g.ClusterId)).ToList();
    }

    public async Task<IReadOnlyList<GoldenRecordVersion>> ListGoldenRecordVersionsAsync(Guid projectId, CancellationToken ct = default)
        => (await LoadAsync(ct)).GoldenRecordVersions.Where(v => v.ProjectId == projectId).ToList();

    public async Task<IReadOnlyList<ReviewTask>> ListReviewTasksAsync(Guid projectId, CancellationToken ct = default)
        => (await LoadAsync(ct)).ReviewTasks.Where(t => t.ProjectId == projectId).ToList();

    public async Task<IReadOnlyList<ClusterMergeEvent>> ListClusterMergeEventsAsync(Guid projectId, CancellationToken ct = default)
        => (await LoadAsync(ct)).ClusterMergeEvents.Where(e => e.ProjectId == projectId).ToList();

    private async Task<ResolutionWorkingSet> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_databasePath))
            return new ResolutionWorkingSet();

        await using var stream = new FileStream(_databasePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<ResolutionWorkingSet>(stream, JsonOptions, ct) ?? new ResolutionWorkingSet();
    }

    private async Task SaveAsync(ResolutionWorkingSet db, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = $"{_databasePath}.{Guid.NewGuid():N}.tmp";
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, db, JsonOptions, ct);
        }

        if (File.Exists(_databasePath))
            File.Replace(tempPath, _databasePath, null);
        else
            File.Move(tempPath, _databasePath);
    }

    private static void ValidateCompletedBatch(ResolutionWorkingSet db, CompletedBatchMetadata completedBatch)
    {
        var duplicateIncomingSourceRecordId = completedBatch.EntityRecords
            .GroupBy(r => (r.ProjectId, SourceRecordId: r.SourceRecordId), new EntityRecordProjectSourceComparer())
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateIncomingSourceRecordId is not null)
            throw new InvalidOperationException($"Duplicate source record id in completed batch: {duplicateIncomingSourceRecordId.Key.SourceRecordId}");

        foreach (var record in completedBatch.EntityRecords)
        {
            if (!db.Projects.Any(p => p.Id == record.ProjectId))
                throw new InvalidOperationException($"Project not found: {record.ProjectId}");
            if (!db.Sources.Any(s => s.Id == record.SourceId && s.ProjectId == record.ProjectId))
                throw new InvalidOperationException($"Source not found for project {record.ProjectId}: {record.SourceId}");
            if (!db.IngestBatches.Any(b => b.Id == record.IngestBatchId && b.ProjectId == record.ProjectId && b.SourceId == record.SourceId))
                throw new InvalidOperationException($"Ingest batch not found for project {record.ProjectId}: {record.IngestBatchId}");
            if (db.EntityRecords.Any(r => r.ProjectId == record.ProjectId && string.Equals(r.SourceRecordId, record.SourceRecordId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Entity record already exists for project {record.ProjectId}: {record.SourceRecordId}");
        }

        var recordIds = completedBatch.EntityRecords.Select(r => r.Id).ToHashSet();
        foreach (var edge in completedBatch.MatchEdges)
        {
            if (!recordIds.Contains(edge.LeftEntityRecordId) || !recordIds.Contains(edge.RightEntityRecordId))
                throw new InvalidOperationException("Match edge references an entity record outside the completed batch.");
            if (!db.IngestBatches.Any(b => b.Id == edge.IngestBatchId && b.ProjectId == edge.ProjectId))
                throw new InvalidOperationException($"Ingest batch not found for project {edge.ProjectId}: {edge.IngestBatchId}");
        }

        foreach (var cluster in completedBatch.Clusters)
        {
            if (!db.Projects.Any(p => p.Id == cluster.ProjectId))
                throw new InvalidOperationException($"Project not found: {cluster.ProjectId}");
            if (cluster.MemberEntityRecordIds.Any(id => !recordIds.Contains(id)))
                throw new InvalidOperationException("Cluster references an entity record outside the completed batch.");
        }

        var goldenIds = completedBatch.GoldenRecords.Select(g => g.Id).ToHashSet();
        foreach (var version in completedBatch.GoldenRecordVersions)
        {
            if (!goldenIds.Contains(version.GoldenRecordId))
                throw new InvalidOperationException("Golden-record version references a golden record outside the completed batch.");
            if (!db.IngestBatches.Any(b => b.Id == version.IngestBatchId && b.ProjectId == version.ProjectId))
                throw new InvalidOperationException($"Ingest batch not found for project {version.ProjectId}: {version.IngestBatchId}");
        }
    }

    private static void ValidateIncrementalRequest(ResolutionWorkingSet db, IncrementalIngestRequest request)
    {
        if (!db.Projects.Any(p => p.Id == request.ProjectId))
            throw new InvalidOperationException($"Project not found: {request.ProjectId}");
        if (!db.Sources.Any(s => s.Id == request.SourceId && s.ProjectId == request.ProjectId))
            throw new InvalidOperationException($"Source not found for project {request.ProjectId}: {request.SourceId}");
        if (!db.IngestBatches.Any(b => b.Id == request.IngestBatchId && b.ProjectId == request.ProjectId && b.SourceId == request.SourceId))
            throw new InvalidOperationException($"Ingest batch not found for project {request.ProjectId}: {request.IngestBatchId}");

        var duplicateIncomingSourceRecordId = request.Records
            .GroupBy(r => r.SourceRecordId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateIncomingSourceRecordId is not null)
            throw new InvalidOperationException($"Duplicate source record id in incremental ingest: {duplicateIncomingSourceRecordId.Key}");

        foreach (var record in request.Records)
        {
            if (record.ProjectId != request.ProjectId || record.SourceId != request.SourceId || record.IngestBatchId != request.IngestBatchId)
                throw new InvalidOperationException("Incremental record provenance does not match the ingest request.");
            if (db.EntityRecords.Any(r => r.ProjectId == request.ProjectId && string.Equals(r.SourceRecordId, record.SourceRecordId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Entity record already exists for project {request.ProjectId}: {record.SourceRecordId}");
        }
    }

    private MatchingProfile ProfileFor(string contentType)
        => _profileProvider.GetProfile(contentType);

    private void EnsureIndexCurrent(ResolutionWorkingSet db)
    {
        if (_index is null)
            return;
        if (_index.Count != db.EntityRecords.Count)
        {
            _index.Rebuild(db.EntityRecords);
            _index.Commit();
        }
    }

    private void IndexRecords(IReadOnlyCollection<EntityRecord> records)
    {
        if (_index is null || records.Count == 0)
            return;
        foreach (var record in records)
            _index.Index(record);
        _index.Commit();
    }

    private IReadOnlyList<string> GenerateEngineBlockingKeys(EntityRecord record, MatchingProfile profile)
        => _resolver.GenerateBlockingKeys(record, profile);

    private CompletedBatchMetadata WithGeneratedBlockingKeys(ResolutionWorkingSet db, CompletedBatchMetadata completedBatch)
    {
        var profilesByProject = completedBatch.EntityRecords
            .Select(r => r.ProjectId)
            .Distinct()
            .ToDictionary(pid => pid, pid => ProfileFor(ContentTypeOf(db, pid)));
        return completedBatch with
        {
            EntityRecords = completedBatch.EntityRecords
                .Select(record => record.BlockingKeys.Count > 0
                    ? record
                    : WithBlockingKeys(record, GenerateEngineBlockingKeys(record, profilesByProject[record.ProjectId])))
                .ToList()
        };
    }

    private static string ContentTypeOf(ResolutionWorkingSet db, Guid projectId)
        => db.Projects.FirstOrDefault(p => p.Id == projectId)?.ContentType ?? "person";

    private void BackfillBlockingKeys(ResolutionWorkingSet db, Guid projectId)
    {
        var profile = ProfileFor(ContentTypeOf(db, projectId));
        for (var i = 0; i < db.EntityRecords.Count; i++)
        {
            var record = db.EntityRecords[i];
            if (record.ProjectId != projectId || record.BlockingKeys.Count > 0)
                continue;

            db.EntityRecords[i] = WithBlockingKeys(record, GenerateEngineBlockingKeys(record, profile));
        }
    }

    private static EntityRecord WithBlockingKeys(EntityRecord record, IReadOnlyList<string> blockingKeys)
        => new()
        {
            Id = record.Id,
            ProjectId = record.ProjectId,
            SourceId = record.SourceId,
            IngestBatchId = record.IngestBatchId,
            SourceRecordId = record.SourceRecordId,
            Fields = record.Fields,
            BlockingKeys = blockingKeys,
            CreatedAt = record.CreatedAt
        };

    private static void ValidateMergeConfiguration(MergeConfiguration? mergeConfiguration)
    {
        if (mergeConfiguration is null)
            return;

        var seenFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in mergeConfiguration.MergeFields)
        {
            if (string.IsNullOrWhiteSpace(field.FieldName))
                throw new ArgumentException("Merge policy fieldName is required.", nameof(mergeConfiguration));
            if (!seenFields.Add(field.FieldName))
                throw new ArgumentException($"Duplicate merge policy field: {field.FieldName}", nameof(mergeConfiguration));
            if (field.SourcePriority is null || field.SourcePriority.Length == 0)
                throw new ArgumentException($"Merge policy field {field.FieldName} requires at least one source priority.", nameof(mergeConfiguration));
            if (field.SourcePriority.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException($"Merge policy field {field.FieldName} contains an empty source priority.", nameof(mergeConfiguration));
        }
    }

    private sealed class FileResolutionContext(ResolutionWorkingSet db) : IResolutionContext
    {
        public IReadOnlyList<EntityRecord> GetLinearCorpus(Guid projectId)
            => db.EntityRecords.Where(r => r.ProjectId == projectId).ToList();

        public IReadOnlyList<Cluster> GetActiveClustersContaining(Guid projectId, IReadOnlyCollection<Guid> recordIds)
            => db.Clusters
                .Where(c => c.ProjectId == projectId && c.Status != "merged"
                            && c.MemberEntityRecordIds.Any(recordIds.Contains))
                .ToList();

        public IReadOnlyList<EntityRecord> GetRecordsByIds(Guid projectId, IReadOnlyCollection<Guid> recordIds)
            => db.EntityRecords.Where(r => r.ProjectId == projectId && recordIds.Contains(r.Id)).ToList();

        public IReadOnlyList<GoldenRecord> GetGoldenRecordsForClusters(Guid projectId, IReadOnlyCollection<Guid> clusterIds)
            => db.GoldenRecords.Where(g => g.ProjectId == projectId && clusterIds.Contains(g.ClusterId)).ToList();

        public IReadOnlyList<GoldenRecordVersion> GetVersionsForGoldenRecords(IReadOnlyCollection<Guid> goldenRecordIds)
            => db.GoldenRecordVersions.Where(v => goldenRecordIds.Contains(v.GoldenRecordId)).ToList();
    }

    private sealed class EntityRecordProjectSourceComparer : IEqualityComparer<(Guid ProjectId, string SourceRecordId)>
    {
        public bool Equals((Guid ProjectId, string SourceRecordId) x, (Guid ProjectId, string SourceRecordId) y)
            => x.ProjectId == y.ProjectId && string.Equals(x.SourceRecordId, y.SourceRecordId, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((Guid ProjectId, string SourceRecordId) obj)
            => HashCode.Combine(obj.ProjectId, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.SourceRecordId));
    }
}
