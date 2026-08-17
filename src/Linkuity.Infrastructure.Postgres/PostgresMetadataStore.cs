using System.Data;
using System.Text.Json;
using Dapper;
using Linkuity.Core.Interfaces;
using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Clustering;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;
using Linkuity.Mdm.Resolution;
using Npgsql;

namespace Linkuity.Infrastructure.Postgres;

public sealed class PostgresMetadataStore : IMetadataStore
{
    private static readonly JsonSerializerOptions JsonOpts = new();

    private readonly string _connectionString;

    private readonly IMatchingEngine _engine;
    private readonly IMatchingProfileProvider _profileProvider;
    private readonly IIndexedCandidateRetrievalStrategy? _index;
    private readonly IncrementalResolver _resolver;

    // Serializes every method that touches _index (a single shared LuceneCandidateRetrieval, per
    // this class's DI registration as a singleton — see PostgresInfrastructureServiceCollectionExtensions).
    // LuceneCandidateRetrieval's own class doc requires this: index mutations (Index/Update/
    // Remove/Rebuild/Commit) "remain sequential and MUST NOT run concurrently with Retrieve" —
    // and Retrieve happens mid-method, inside _resolver.Resolve, between this class's own
    // EnsureIndexCurrentAsync call and its later index-mutation calls. So the gate has to span
    // the whole method, not just the calls that touch _index directly, mirroring
    // FileMetadataStore's identical whole-method _gate (there, protecting the JSON file instead).
    // Unlike FileMetadataStore, Postgres's own transaction already gives the SQL work its own
    // concurrency safety, so this serializes only what the shared Lucene writer requires — not a
    // deliberate throughput tradeoff on the SQL side, just the narrowest correct scope.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PostgresMetadataStore(
        PostgresMetadataStoreOptions options,
        IMatchingEngine? engine,
        IMatchingProfileProvider? profileProvider,
        IIndexedCandidateRetrievalStrategy? indexedRetrieval)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new ArgumentException("Connection string is required.", nameof(options));

        _connectionString = options.ConnectionString;
        _index = indexedRetrieval;
        _engine = indexedRetrieval is not null
            ? MatchingDefaults.CreateEngine(indexedRetrieval)
            : (engine ?? MatchingDefaults.CreateEngine());
        _profileProvider = profileProvider
            ?? new DefaultMatchingProfileProvider(DefaultMatchingProfileProvider.BuiltInProfiles());
        // CohesionClusterMergePolicy is stateless (the threshold it reads lives on the profile
        // passed at Resolve time, not on the policy instance), so constructing one here rather than
        // taking it through DI costs nothing behaviorally while keeping this constructor's public
        // shape unchanged for every existing caller.
        _resolver = new IncrementalResolver(_engine, indexedRetrieval is not null, new CohesionClusterMergePolicy(), options.IngestParallelism);
    }

    // ──────────────────────────────── connection ────────────────────────────────

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    // ──────────────────────────────── Projects ──────────────────────────────────

    public async Task<Project> CreateProjectAsync(
        string name, string contentType, MergeConfiguration? mergeConfiguration,
        DateTimeOffset createdAt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Project name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(contentType))
            throw new ArgumentException("Project content type is required.", nameof(contentType));
        ValidateMergeConfiguration(mergeConfiguration);

        await using var conn = await OpenConnectionAsync(ct);

        var count = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM projects WHERE lower(name) = lower(@Name)",
            new { Name = name }, cancellationToken: ct));
        if (count > 0)
            throw new InvalidOperationException($"Project already exists: {name}");

        var id = Guid.NewGuid();
        var mcJson = mergeConfiguration is null
            ? null
            : JsonSerializer.Serialize(mergeConfiguration, JsonOpts);

        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO projects (id, name, content_type, merge_configuration, created_at)
            VALUES (@Id, @Name, @ContentType, @McJson::jsonb, @CreatedAt)
            """,
            new { Id = id, Name = name, ContentType = contentType, McJson = mcJson, CreatedAt = createdAt },
            cancellationToken: ct));

        return new Project
        {
            Id = id,
            Name = name,
            ContentType = contentType,
            MergeConfiguration = mergeConfiguration,
            CreatedAt = createdAt
        };
    }

    public async Task<Project> UpdateProjectMergePolicyAsync(
        Guid projectId, MergeConfiguration? mergeConfiguration, CancellationToken ct = default)
    {
        ValidateMergeConfiguration(mergeConfiguration);

        await using var conn = await OpenConnectionAsync(ct);
        var mcJson = mergeConfiguration is null
            ? null
            : JsonSerializer.Serialize(mergeConfiguration, JsonOpts);

        var rows = (await conn.QueryAsync<ProjectRow>(new CommandDefinition(
            """
            UPDATE projects
            SET merge_configuration = @McJson::jsonb
            WHERE id = @Id
            RETURNING
                id                             AS "Id",
                name                           AS "Name",
                content_type                   AS "ContentType",
                merge_configuration::text      AS "MergeConfigurationJson",
                created_at                     AS "CreatedAt"
            """,
            new { Id = projectId, McJson = mcJson },
            cancellationToken: ct))).ToList();

        if (rows.Count == 0)
            throw new InvalidOperationException($"Project not found: {projectId}");

        return MapProject(rows[0]);
    }

    public async Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ProjectRow>(new CommandDefinition(
            """
            SELECT
                id                         AS "Id",
                name                       AS "Name",
                content_type               AS "ContentType",
                merge_configuration::text  AS "MergeConfigurationJson",
                created_at                 AS "CreatedAt"
            FROM projects
            ORDER BY created_at
            """,
            cancellationToken: ct));
        return rows.Select(MapProject).ToList();
    }

    public async Task<Project?> GetProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ProjectRow>(new CommandDefinition(
            """
            SELECT
                id                         AS "Id",
                name                       AS "Name",
                content_type               AS "ContentType",
                merge_configuration::text  AS "MergeConfigurationJson",
                created_at                 AS "CreatedAt"
            FROM projects
            WHERE id = @Id
            """,
            new { Id = projectId },
            cancellationToken: ct));
        var row = rows.FirstOrDefault();
        return row is null ? null : MapProject(row);
    }

    // ──────────────────────────────── Sources ───────────────────────────────────

    public async Task<Source> CreateSourceAsync(
        Guid projectId, string name, DateTimeOffset createdAt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Source name is required.", nameof(name));

        await using var conn = await OpenConnectionAsync(ct);

        var projectCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM projects WHERE id = @Id",
            new { Id = projectId }, cancellationToken: ct));
        if (projectCount == 0)
            throw new InvalidOperationException($"Project not found: {projectId}");

        var sourceCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM sources WHERE project_id = @ProjectId AND lower(name) = lower(@Name)",
            new { ProjectId = projectId, Name = name }, cancellationToken: ct));
        if (sourceCount > 0)
            throw new InvalidOperationException($"Source already exists for project {projectId}: {name}");

        var id = Guid.NewGuid();
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO sources (id, project_id, name, created_at) VALUES (@Id, @ProjectId, @Name, @CreatedAt)",
            new { Id = id, ProjectId = projectId, Name = name, CreatedAt = createdAt },
            cancellationToken: ct));

        return new Source { Id = id, ProjectId = projectId, Name = name, CreatedAt = createdAt };
    }

    public async Task<IReadOnlyList<Source>> ListSourcesAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SourceRow>(new CommandDefinition(
            """
            SELECT id AS "Id", project_id AS "ProjectId", name AS "Name", created_at AS "CreatedAt"
            FROM sources
            WHERE project_id = @ProjectId
            ORDER BY created_at
            """,
            new { ProjectId = projectId }, cancellationToken: ct));
        return rows.Select(MapSource).ToList();
    }

    public async Task<Source?> GetSourceAsync(Guid sourceId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SourceRow>(new CommandDefinition(
            "SELECT id AS \"Id\", project_id AS \"ProjectId\", name AS \"Name\", created_at AS \"CreatedAt\" FROM sources WHERE id = @Id",
            new { Id = sourceId }, cancellationToken: ct));
        var row = rows.FirstOrDefault();
        return row is null ? null : MapSource(row);
    }

    // ──────────────────────────────── IngestBatches ─────────────────────────────

    public async Task<IngestBatch> CreateIngestBatchAsync(
        Guid projectId, Guid sourceId, Guid? jobId, int recordCount,
        DateTimeOffset createdAt, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        var projectCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM projects WHERE id = @Id",
            new { Id = projectId }, cancellationToken: ct));
        if (projectCount == 0)
            throw new InvalidOperationException($"Project not found: {projectId}");

        var sourceCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM sources WHERE id = @SourceId AND project_id = @ProjectId",
            new { SourceId = sourceId, ProjectId = projectId }, cancellationToken: ct));
        if (sourceCount == 0)
            throw new InvalidOperationException($"Source not found for project {projectId}: {sourceId}");

        var id = Guid.NewGuid();
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ingest_batches (id, project_id, source_id, job_id, record_count, created_at)
            VALUES (@Id, @ProjectId, @SourceId, @JobId, @RecordCount, @CreatedAt)
            """,
            new { Id = id, ProjectId = projectId, SourceId = sourceId, JobId = jobId, RecordCount = recordCount, CreatedAt = createdAt },
            cancellationToken: ct));

        return new IngestBatch
        {
            Id = id,
            ProjectId = projectId,
            SourceId = sourceId,
            JobId = jobId,
            RecordCount = recordCount,
            CreatedAt = createdAt
        };
    }

    public async Task<IReadOnlyList<IngestBatch>> ListIngestBatchesAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<IngestBatchRow>(new CommandDefinition(
            """
            SELECT
                id           AS "Id",
                project_id   AS "ProjectId",
                source_id    AS "SourceId",
                job_id       AS "JobId",
                record_count AS "RecordCount",
                created_at   AS "CreatedAt"
            FROM ingest_batches
            WHERE project_id = @ProjectId
            ORDER BY created_at
            """,
            new { ProjectId = projectId }, cancellationToken: ct));
        return rows.Select(MapIngestBatch).ToList();
    }

    // ──────────────────────────────── Completed Batch (T12) ────────────────────

    public async Task SaveCompletedBatchAsync(CompletedBatchMetadata completedBatch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(completedBatch);

        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

            // Load projects to get contentType (blocking keys) and MergeConfiguration (resolver).
            var projectIds = completedBatch.EntityRecords
                .Select(r => r.ProjectId)
                .Distinct()
                .ToArray();
            var projects = await LoadProjectsAsync(conn, tx, projectIds, ct);
            var projectMap = projects.ToDictionary(p => p.Id);

            // Blocking-key backfill — mirror WithGeneratedBlockingKeys.
            var recordsWithKeys = completedBatch.EntityRecords
                .Select(record =>
                {
                    var contentType = projectMap.TryGetValue(record.ProjectId, out var proj)
                        ? proj.ContentType : "person";
                    var profile = _profileProvider.GetProfile(contentType);
                    return _engine.PrepareForStorage(record, profile);
                })
                .ToList();
            var completedBatchWithKeys = completedBatch with { EntityRecords = recordsWithKeys };

            // Validate inside the transaction — throws roll back via await using.
            await ValidateCompletedBatchAsync(conn, tx, completedBatchWithKeys, ct);

            // Repair any pre-existing drift before this call adds to the index, matching
            // SaveIncrementalIngestAsync (step 6) and DeleteRecordsAsync. Without it the
            // bulk-import path could never self-heal: a batch-only workflow never calls either of
            // those, so a drift left by an earlier crash (or by this method's own IndexRecords
            // failing after the commit succeeded — see below) would persist indefinitely. This must
            // run BEFORE the mutation applier: afterwards the transaction already holds this
            // batch's rows, which are not yet indexed, so the counts would disagree on every call
            // and trigger a needless full rebuild every time.
            await EnsureIndexCurrentAsync(conn, tx, ct);

            // Resolve using the shared CompletedBatchResolver.
            var mutations = CompletedBatchResolver.Resolve(completedBatchWithKeys, projects, _profileProvider, DateTimeOffset.UtcNow);

            // Apply all mutations within the same transaction.
            await new PostgresMutationApplier(conn, tx).ApplyAsync(mutations, ct);

            // The SQL transaction commits BEFORE the index mutation, matching
            // SaveIncrementalIngestAsync (steps 11/12) and DeleteRecordsAsync. Nothing between here
            // and the commit reads the index (CompletedBatchResolver.Resolve already ran, and it
            // takes its edges and clusters from the batch itself rather than retrieving
            // candidates), so this ordering costs nothing; it only decides which store is at risk
            // if the other write fails.
            //
            // NOTE the discriminator here is NOT detectability, unlike the correction path. A
            // completed batch is a pure insert, so either ordering leaves a live-count mismatch
            // that EnsureIndexCurrentAsync could see (a correction's supersede-plus-add is net-zero
            // and cannot be seen either way — that argument belongs to SaveIncrementalIngestAsync,
            // not here). What differs is what the index serves in the meantime.
            // LuceneCandidateRetrieval.Retrieve reconstructs candidates FROM INDEX DOCUMENTS rather
            // than looking them up in the store, so indexing first and then failing the commit
            // makes retrieval hand out records that never committed at all, which then get scored
            // into edges and clusters referencing ids nothing can resolve. Committing first inverts
            // that into the conservative failure: the rows exist but are temporarily unsearchable,
            // costing missed matches rather than phantom ones.
            //
            // Caveat worth knowing before relying on self-healing: unlike the two paths above, this
            // method does not itself call EnsureIndexCurrentAsync, so a drift left by a failed
            // IndexRecords below is only repaired once some later ingest or deletion call runs
            // against the same store — a batch-only workflow never repairs it on its own.
            await tx.CommitAsync(ct);

            // Lucene commit is separate from the SQL txn.
            if (_index is not null)
                IndexRecords(completedBatchWithKeys.EntityRecords);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IncrementalIngestResult> SaveIncrementalIngestAsync(IncrementalIngestRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _gate.WaitAsync(ct);
        try
        {
            // 1. Open the connection threshold validation (step 2) and the transaction (step 3)
            //    both use — one connection either way, so opening it here costs nothing extra.
            await using var conn = await OpenConnectionAsync(ct);

            // 2. Threshold validation — before opening the txn, as it always was. Shared with
            //    FileMetadataStore rather than copied into it, which is how the two previously stayed
            //    in step by hand. This needs the profile's resolved scorer's scale (an evidence-scored
            //    profile's thresholds are bits of log-odds evidence, invalid against the default
            //    UnitInterval scale), which needs only the project's content type — a single
            //    untransacted read, far cheaper than the ReadCommitted transaction plus step 4's
            //    provenance round-trips this check must precede. A project that does not exist skips
            //    this: there is no profile to resolve a scale from, and step 4's
            //    ValidateIncrementalRequestAsync reports "Project not found", the more specific error
            //    for that case. (A request invalid on BOTH grounds now surfaces the project error
            //    instead of the threshold error — an unavoidable consequence of thresholds only being
            //    meaningful relative to a scale the project's own profile supplies, not a regression
            //    from before this scale threading existed: that version didn't validate evidence
            //    profiles' thresholds correctly at all.)
            var contentType = await LoadProjectContentTypeAsync(conn, request.ProjectId, ct);
            if (contentType is not null)
                _ = IncrementalResolver.ThresholdsFor(request, _engine.ScaleOf(_profileProvider.GetProfile(contentType)));

            // 3. One READ COMMITTED transaction for the whole bounded ingest.
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

            // 4. Targeted provenance + duplicate-source-record-id validation (throws roll back via await using).
            await ValidateIncrementalRequestAsync(conn, tx, request, ct);

            // 5. Load the project + its matching profile.
            var projects = await LoadProjectsAsync(conn, tx, [request.ProjectId], ct);
            var project = projects.Count > 0
                ? projects[0]
                : throw new InvalidOperationException($"Project not found: {request.ProjectId}");
            var profile = _profileProvider.GetProfile(project.ContentType);

            // 6. EnsureIndexCurrent: COUNT(*) of entity_records vs the index. On the normal path the
            //    counts match (records are indexed as they are inserted) and nothing happens — no scan.
            //    A mismatch is the explicit recovery path (full rebuild); it must not fire in the tests.
            await EnsureIndexCurrentAsync(conn, tx, ct);

            // 7. Build incoming records with blocking keys (generated only when absent). No full backfill —
            //    blocking keys are stored at insert time on Postgres, so there is no scan over existing rows.
            var incomingRecords = request.Records
                .Select(record => _engine.PrepareForStorage(record, profile))
                .ToList();

            // 8. Classify corrections/no-ops out of the incoming batch, detaching corrected records
            //    from their clusters before matching runs — mirrors FileMetadataStore.
            //    SaveIncrementalIngestAsync's identical two-phase shape.
            var context = new PostgresResolutionContext(conn, tx);
            var now = DateTimeOffset.UtcNow;
            var (recordsToResolve, correctionMutations) = _resolver.ClassifyAndDetachCorrections(
                project, profile, incomingRecords, context, now);

            await new PostgresMutationApplier(conn, tx).ApplyAsync(correctionMutations, ct);

            // 9. Resolve through the bounded PostgresResolutionContext (Lucene supplies candidates).
            var (result, mutations) = _resolver.Resolve(
                request, project, profile, recordsToResolve, context, now);

            // 10. Apply the targeted mutation set within the same transaction.
            await new PostgresMutationApplier(conn, tx).ApplyAsync(mutations, ct);

            // 10b. Reconcile the batch's stored record_count with the rows actually ingested in this
            //      call — recordsToResolve.Count (new + corrected; no-ops already excluded by
            //      ClassifyAndDetachCorrections), not the raw request.Records.Count, matching the file
            //      store's #70 fix (a corrected record must not be double-counted, and a dropped no-op
            //      resend must not inflate the stored count).
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE ingest_batches SET record_count = @n WHERE id = @batchId",
                new { n = recordsToResolve.Count, batchId = request.IngestBatchId },
                transaction: tx, cancellationToken: ct));

            // RecordsAdded/RecordsCorrected split matches the file store: result.RecordsAdded comes
            // from Resolve's incomingRecords parameter, which IS recordsToResolve here (new AND
            // corrected records; no-ops already excluded), so the corrected count must be subtracted
            // or it would be double-counted as both "added" and "corrected".
            var correctedCount = correctionMutations.CorrectionEventsToInsert.Count;

            // 11. Commit the SQL transaction.
            await tx.CommitAsync(ct);

            // 12. Index the incoming records AFTER the SQL commit succeeds (Lucene commit is
            //     separate from the SQL txn; retrieval only ever observes committed Lucene state, so
            //     this ordering changes nothing about what step 9's Resolve could see — it only
            //     determines which store is at risk if the other write fails). Indexing before the
            //     SQL commit would be worse for a DELETION: a live-count mismatch on the next call
            //     could detect and repair a stale-but-real index entry left by that ordering. For a
            //     CORRECTION specifically, superseded-doc removal plus new-doc addition is a net-zero
            //     change in live document count either way, so EnsureIndexCurrentAsync's count
            //     comparison cannot detect drift here regardless of ordering — but committing SQL
            //     first at least avoids the worse failure mode of Lucene durably serving a "corrected"
            //     record that, had the SQL commit then failed, never actually existed in the store.
            if (_index is not null)
            {
                foreach (var evt in correctionMutations.CorrectionEventsToInsert)
                    _index.Remove(evt.SupersededEntityRecordId);
                IndexRecords(recordsToResolve);
            }

            // 13. Return.
            return result with { RecordsAdded = result.RecordsAdded - correctedCount, RecordsCorrected = correctedCount };
        }
        finally
        {
            _gate.Release();
        }
    }

    // Untransacted, single-row lookup used only to resolve the project's matching profile (and
    // therefore its scorer's ScoreScale) before SaveIncrementalIngestAsync opens its own
    // transaction — see step 2 above. Null when the project does not exist, in which case there
    // is nothing to resolve a scale from and the caller defers to the transactional provenance
    // check instead.
    private async Task<string?> LoadProjectContentTypeAsync(NpgsqlConnection conn, Guid projectId, CancellationToken ct)
        => await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT content_type FROM projects WHERE id = @ProjectId",
            new { ProjectId = projectId }, cancellationToken: ct));

    private async Task EnsureIndexCurrentAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        if (_index is null)
            return;

        // Superseded/deleted records are tombstoned in place (superseded_at/deleted_at), never
        // removed from entity_records, but they ARE removed from the live Lucene index — so only
        // live rows count on either side of this comparison. Comparing against the raw table
        // count would see a permanent mismatch after any correction/deletion and rebuild from
        // every row including tombstoned ones, undoing the exclusion.
        var count = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM entity_records WHERE superseded_at IS NULL AND deleted_at IS NULL",
            transaction: tx, cancellationToken: ct));
        if (_index.Count == count)
            return; // normal path — index spans the store and is current.

        // Recovery path only (count drift). This is the SINGLE unbounded entity_records read in the
        // class and is unreachable on the normal ingest path; it rebuilds the derived Lucene artifact.
        var all = (await conn.QueryAsync<EntityRecordRow>(new CommandDefinition(
            """
            SELECT
                id                                 AS "Id",
                project_id                         AS "ProjectId",
                source_id                          AS "SourceId",
                ingest_batch_id                    AS "IngestBatchId",
                source_record_id                   AS "SourceRecordId",
                fields::text                       AS "FieldsJson",
                array_to_json(blocking_keys)::text AS "BlockingKeysJson",
                created_at                         AS "CreatedAt"
            FROM entity_records
            WHERE superseded_at IS NULL AND deleted_at IS NULL
            """, transaction: tx, cancellationToken: ct))).Select(MapEntityRecord).ToList();
        _index.Rebuild(all);
        _index.Commit();
    }

    private async Task ValidateIncrementalRequestAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, IncrementalIngestRequest request, CancellationToken ct)
    {
        // Provenance: project / source / ingest-batch exist.
        if (!await ExistsAsync(conn, tx,
                "SELECT EXISTS(SELECT 1 FROM projects WHERE id = @p0)",
                [("p0", request.ProjectId)], ct))
            throw new InvalidOperationException($"Project not found: {request.ProjectId}");

        if (!await ExistsAsync(conn, tx,
                "SELECT EXISTS(SELECT 1 FROM sources WHERE id = @p0 AND project_id = @p1)",
                [("p0", request.SourceId), ("p1", request.ProjectId)], ct))
            throw new InvalidOperationException(
                $"Source not found for project {request.ProjectId}: {request.SourceId}");

        if (!await ExistsAsync(conn, tx,
                "SELECT EXISTS(SELECT 1 FROM ingest_batches WHERE id = @p0 AND project_id = @p1 AND source_id = @p2)",
                [("p0", request.IngestBatchId), ("p1", request.ProjectId), ("p2", request.SourceId)], ct))
            throw new InvalidOperationException(
                $"Ingest batch not found for project {request.ProjectId}: {request.IngestBatchId}");

        // Duplicate source-record-id within the incoming set (in-memory, case-insensitive).
        var duplicate = request.Records
            .GroupBy(r => r.SourceRecordId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException(
                $"Duplicate source record id in incremental ingest: {duplicate.Key}");

        // Per-record provenance must match the request.
        foreach (var record in request.Records)
        {
            if (record.ProjectId != request.ProjectId ||
                record.SourceId != request.SourceId ||
                record.IngestBatchId != request.IngestBatchId)
                throw new InvalidOperationException("Incremental record provenance does not match the ingest request.");
        }
    }

    // ──────────────────────────────── Entity Records ────────────────────────────

    public async Task<IReadOnlyList<EntityRecord>> ListEntityRecordsAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<EntityRecordRow>(new CommandDefinition(
            """
            SELECT
                id                                          AS "Id",
                project_id                                  AS "ProjectId",
                source_id                                   AS "SourceId",
                ingest_batch_id                             AS "IngestBatchId",
                source_record_id                            AS "SourceRecordId",
                fields::text                                AS "FieldsJson",
                array_to_json(blocking_keys)::text          AS "BlockingKeysJson",
                created_at                                  AS "CreatedAt"
            FROM entity_records
            WHERE project_id = @ProjectId AND superseded_at IS NULL AND deleted_at IS NULL
            """,
            new { ProjectId = projectId }, cancellationToken: ct));
        return rows.Select(MapEntityRecord).ToList();
    }

    // ──────────────────────────────── Match Edges ───────────────────────────────

    public async Task<IReadOnlyList<MatchEdge>> ListMatchEdgesAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<MatchEdgeRow>(new CommandDefinition(
            """
            SELECT
                id                      AS "Id",
                project_id              AS "ProjectId",
                ingest_batch_id         AS "IngestBatchId",
                left_entity_record_id   AS "LeftEntityRecordId",
                right_entity_record_id  AS "RightEntityRecordId",
                score                   AS "Score",
                method                  AS "Method",
                decision                AS "Decision",
                breakdown::text         AS "BreakdownJson",
                scorer                  AS "Scorer",
                profile_content_type    AS "ProfileContentType",
                profile_fingerprint     AS "ProfileFingerprint",
                created_at              AS "CreatedAt"
            FROM match_edges
            WHERE project_id = @ProjectId
            """,
            new { ProjectId = projectId }, cancellationToken: ct));
        return rows.Select(MapMatchEdge).ToList();
    }

    // ──────────────────────────────── Clusters ──────────────────────────────────

    public async Task<IReadOnlyList<Cluster>> ListClustersAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        var clusterRows = (await conn.QueryAsync<ClusterRow>(new CommandDefinition(
            """
            SELECT
                id                      AS "Id",
                project_id              AS "ProjectId",
                status                  AS "Status",
                merged_into_cluster_id  AS "MergedIntoClusterId",
                comparisons_inside      AS "ComparisonsInside",
                agreements_inside       AS "AgreementsInside",
                created_at              AS "CreatedAt"
            FROM clusters
            WHERE project_id = @ProjectId
              AND status != 'merged'
            """,
            new { ProjectId = projectId }, cancellationToken: ct))).ToList();

        if (clusterRows.Count == 0)
            return [];

        // Hydrate MemberEntityRecordIds — two-query batch to avoid N+1.
        var memberRows = (await conn.QueryAsync<ClusterMemberRow>(new CommandDefinition(
            """
            SELECT cluster_id AS "ClusterId", id AS "RecordId"
            FROM entity_records
            WHERE cluster_id IN (
                SELECT id FROM clusters
                WHERE project_id = @ProjectId AND status != 'merged'
            )
            """,
            new { ProjectId = projectId }, cancellationToken: ct))).ToList();

        var membersByCluster = memberRows
            .GroupBy(m => m.ClusterId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(m => m.RecordId).ToList());

        return clusterRows.Select(r => MapCluster(r, membersByCluster)).ToList();
    }

    // ──────────────────────────────── Golden Records ────────────────────────────

    public async Task<IReadOnlyList<GoldenRecord>> ListGoldenRecordsAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<GoldenRecordRow>(new CommandDefinition(
            """
            SELECT
                g.id                 AS "Id",
                g.project_id         AS "ProjectId",
                g.cluster_id         AS "ClusterId",
                g.current_version_id AS "CurrentVersionId",
                g.fields::text       AS "FieldsJson",
                g.updated_at         AS "UpdatedAt"
            FROM golden_records g
            JOIN clusters c ON c.id = g.cluster_id
            WHERE g.project_id = @ProjectId
              AND c.status != 'merged'
            """,
            new { ProjectId = projectId }, cancellationToken: ct));
        return rows.Select(MapGoldenRecord).ToList();
    }

    // ──────────────────────────────── Golden Record Versions ────────────────────

    public async Task<IReadOnlyList<GoldenRecordVersion>> ListGoldenRecordVersionsAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<GoldenRecordVersionRow>(new CommandDefinition(
            """
            SELECT
                id               AS "Id",
                golden_record_id AS "GoldenRecordId",
                project_id       AS "ProjectId",
                cluster_id       AS "ClusterId",
                ingest_batch_id  AS "IngestBatchId",
                version_number   AS "VersionNumber",
                fields::text     AS "FieldsJson",
                created_at       AS "CreatedAt"
            FROM golden_record_versions
            WHERE project_id = @ProjectId
            """,
            new { ProjectId = projectId }, cancellationToken: ct));
        return rows.Select(MapGoldenRecordVersion).ToList();
    }

    // ──────────────────────────────── Review Tasks ──────────────────────────────

    public async Task<IReadOnlyList<ReviewTask>> ListReviewTasksAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ReviewTaskRow>(new CommandDefinition(
            """
            SELECT
                id                          AS "Id",
                project_id                  AS "ProjectId",
                ingest_batch_id             AS "IngestBatchId",
                new_entity_record_id        AS "NewEntityRecordId",
                candidate_entity_record_id  AS "CandidateEntityRecordId",
                score                       AS "Score",
                reason                      AS "Reason",
                status                      AS "Status",
                breakdown::text             AS "BreakdownJson",
                left_cluster_id             AS "LeftClusterId",
                right_cluster_id            AS "RightClusterId",
                created_at                  AS "CreatedAt"
            FROM review_tasks
            WHERE project_id = @ProjectId
            """,
            new { ProjectId = projectId }, cancellationToken: ct));
        return rows.Select(MapReviewTask).ToList();
    }

    // ──────────────────────────────── Cluster Merge Events ──────────────────────

    public async Task<IReadOnlyList<ClusterMergeEvent>> ListClusterMergeEventsAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ClusterMergeEventRow>(new CommandDefinition(
            """
            SELECT
                id                                                          AS "Id",
                project_id                                                  AS "ProjectId",
                survivor_cluster_id                                         AS "SurvivorClusterId",
                absorbed_cluster_id                                         AS "AbsorbedClusterId",
                array_to_json(absorbed_member_entity_record_ids)::text      AS "AbsorbedMemberIdsJson",
                array_to_json(trigger_record_ids)::text                     AS "TriggerRecordIdsJson",
                score                                                       AS "Score",
                breakdown::text                                             AS "BreakdownJson",
                ingest_batch_id                                             AS "IngestBatchId",
                created_at                                                  AS "CreatedAt"
            FROM cluster_merge_events
            WHERE project_id = @ProjectId
            """,
            new { ProjectId = projectId }, cancellationToken: ct));
        return rows.Select(MapClusterMergeEvent).ToList();
    }

    public async Task<IReadOnlyList<RecordCorrectedEvent>> ListRecordCorrectedEventsAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RecordCorrectedEventRow>(new CommandDefinition(
            """
            SELECT
                id                            AS "Id",
                project_id                    AS "ProjectId",
                superseded_entity_record_id   AS "SupersededEntityRecordId",
                corrected_entity_record_id    AS "CorrectedEntityRecordId",
                previous_fields::text         AS "PreviousFieldsJson",
                new_fields::text              AS "NewFieldsJson",
                previous_cluster_id           AS "PreviousClusterId",
                ingest_batch_id               AS "IngestBatchId",
                created_at                    AS "CreatedAt"
            FROM record_corrected_events
            WHERE project_id = @ProjectId
            """,
            new { ProjectId = projectId }, cancellationToken: ct));
        return rows.Select(MapRecordCorrectedEvent).ToList();
    }

    public async Task<RecordDeletionResult> DeleteRecordsAsync(
        Guid projectId, Guid sourceId, Guid ingestBatchId, IReadOnlyList<string> sourceRecordIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourceRecordIds);
        if (sourceRecordIds.Count == 0)
            throw new ArgumentException("At least one source record id is required.", nameof(sourceRecordIds));

        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

            await ValidateDeletionRequestAsync(conn, tx, projectId, sourceId, ingestBatchId, sourceRecordIds, ct);
            await EnsureIndexCurrentAsync(conn, tx, ct);

            var projects = await LoadProjectsAsync(conn, tx, [projectId], ct);
            var project = projects.Count > 0
                ? projects[0]
                : throw new InvalidOperationException($"Project not found: {projectId}");
            var profile = _profileProvider.GetProfile(project.ContentType);
            var context = new PostgresResolutionContext(conn, tx);
            var now = DateTimeOffset.UtcNow;

            var (deletedIds, mutations) = _resolver.ClassifyAndDetachDeletions(
                project, profile, sourceRecordIds, ingestBatchId, context, now);

            await new PostgresMutationApplier(conn, tx).ApplyAsync(mutations, ct);

            // The SQL commit happens BEFORE the index mutation — see the matching comment in
            // SaveIncrementalIngestAsync (step 11/12) for why this ordering, not the reverse, is the
            // one with a self-healing recovery path via EnsureIndexCurrentAsync.
            await tx.CommitAsync(ct);

            if (_index is not null)
            {
                foreach (var id in deletedIds)
                    _index.Remove(id);
                _index.Commit();
            }

            return new RecordDeletionResult(deletedIds.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task ValidateDeletionRequestAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid projectId, Guid sourceId, Guid ingestBatchId,
        IReadOnlyList<string> sourceRecordIds, CancellationToken ct)
    {
        if (!await ExistsAsync(conn, tx,
                "SELECT EXISTS(SELECT 1 FROM projects WHERE id = @p0)",
                [("p0", projectId)], ct))
            throw new InvalidOperationException($"Project not found: {projectId}");

        if (!await ExistsAsync(conn, tx,
                "SELECT EXISTS(SELECT 1 FROM sources WHERE id = @p0 AND project_id = @p1)",
                [("p0", sourceId), ("p1", projectId)], ct))
            throw new InvalidOperationException($"Source not found for project {projectId}: {sourceId}");

        if (!await ExistsAsync(conn, tx,
                "SELECT EXISTS(SELECT 1 FROM ingest_batches WHERE id = @p0 AND project_id = @p1 AND source_id = @p2)",
                [("p0", ingestBatchId), ("p1", projectId), ("p2", sourceId)], ct))
            throw new InvalidOperationException($"Ingest batch not found for project {projectId}: {ingestBatchId}");

        var duplicate = sourceRecordIds
            .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate source record id in deletion request: {duplicate.Key}");
    }

    public async Task<IReadOnlyList<RecordDeletedEvent>> ListRecordDeletedEventsAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RecordDeletedEventRow>(new CommandDefinition(
            """
            SELECT
                id                         AS "Id",
                project_id                 AS "ProjectId",
                deleted_entity_record_id   AS "DeletedEntityRecordId",
                previous_fields::text      AS "PreviousFieldsJson",
                previous_cluster_id        AS "PreviousClusterId",
                ingest_batch_id            AS "IngestBatchId",
                created_at                 AS "CreatedAt"
            FROM record_deleted_events
            WHERE project_id = @ProjectId
            """,
            new { ProjectId = projectId }, cancellationToken: ct));
        return rows.Select(MapRecordDeletedEvent).ToList();
    }

    // ──────────────────────────────── T12 helpers ───────────────────────────────

    private static async Task<List<Project>> LoadProjectsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid[] projectIds, CancellationToken ct)
    {
        if (projectIds.Length == 0)
            return [];

        var projects = new List<Project>(projectIds.Length);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT
                id                        AS id,
                name                      AS name,
                content_type              AS content_type,
                merge_configuration::text AS merge_config_json,
                created_at                AS created_at
            FROM projects
            WHERE id = ANY(@ids)
            """, conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid)
        { Value = projectIds });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var mcJson = reader.IsDBNull(3) ? null : reader.GetString(3);
            projects.Add(new Project
            {
                Id = reader.GetGuid(0),
                Name = reader.GetString(1),
                ContentType = reader.GetString(2),
                MergeConfiguration = mcJson is null
                    ? null
                    : JsonSerializer.Deserialize<MergeConfiguration>(mcJson, JsonOpts),
                CreatedAt = new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero)
            });
        }
        return projects;
    }

    private static async Task ValidateCompletedBatchAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        CompletedBatchMetadata batch, CancellationToken ct)
    {
        // 1. Duplicate source-record-id within the batch (per project, case-insensitive).
        var seen = new Dictionary<(Guid ProjectId, string LowerSrcId), string>();
        foreach (var record in batch.EntityRecords)
        {
            var key = (record.ProjectId, record.SourceRecordId.ToLowerInvariant());
            if (seen.ContainsKey(key))
                throw new InvalidOperationException(
                    $"Duplicate source record id in completed batch: {record.SourceRecordId}");
            seen[key] = record.SourceRecordId;
        }

        // 2. Per-record: project/source/batch exist; entity_record not already present (done below per-project).
        foreach (var record in batch.EntityRecords)
        {
            if (!await ExistsAsync(conn, tx,
                    "SELECT EXISTS(SELECT 1 FROM projects WHERE id = @p0)",
                    [("p0", record.ProjectId)], ct))
                throw new InvalidOperationException($"Project not found: {record.ProjectId}");

            if (!await ExistsAsync(conn, tx,
                    "SELECT EXISTS(SELECT 1 FROM sources WHERE id = @p0 AND project_id = @p1)",
                    [("p0", record.SourceId), ("p1", record.ProjectId)], ct))
                throw new InvalidOperationException(
                    $"Source not found for project {record.ProjectId}: {record.SourceId}");

            if (!await ExistsAsync(conn, tx,
                    "SELECT EXISTS(SELECT 1 FROM ingest_batches WHERE id = @p0 AND project_id = @p1 AND source_id = @p2)",
                    [("p0", record.IngestBatchId), ("p1", record.ProjectId), ("p2", record.SourceId)], ct))
                throw new InvalidOperationException(
                    $"Ingest batch not found for project {record.ProjectId}: {record.IngestBatchId}");
        }

        // 3. No existing entity_record with same (project_id, lower(source_record_id)).
        //    Report the INCOMING source_record_id (parity with FileMetadataStore) by reconstructing
        //    it from a lower→incoming map — the same technique used in ValidateIncrementalRequestAsync.
        foreach (var group in batch.EntityRecords.GroupBy(r => r.ProjectId))
        {
            var lowerToIncoming = group
                .GroupBy(r => r.SourceRecordId.ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.First().SourceRecordId);
            await using var cmd = new NpgsqlCommand(
                """
                SELECT lower(source_record_id)
                FROM entity_records
                WHERE project_id = @pid AND lower(source_record_id) = ANY(@ids)
                LIMIT 1
                """, conn, tx);
            cmd.Parameters.AddWithValue("pid", group.Key);
            cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text)
            { Value = lowerToIncoming.Keys.ToArray() });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var matchedLower = reader.GetString(0);
                var incomingSourceRecordId = lowerToIncoming.TryGetValue(matchedLower, out var src) ? src : matchedLower;
                throw new InvalidOperationException(
                    $"Entity record already exists for project {group.Key}: {incomingSourceRecordId}");
            }
        }

        // 4. Edges reference in-batch record ids; their batch exists.
        var recordIds = batch.EntityRecords.Select(r => r.Id).ToHashSet();
        foreach (var edge in batch.MatchEdges)
        {
            if (!recordIds.Contains(edge.LeftEntityRecordId) || !recordIds.Contains(edge.RightEntityRecordId))
                throw new InvalidOperationException(
                    "Match edge references an entity record outside the completed batch.");

            if (!await ExistsAsync(conn, tx,
                    "SELECT EXISTS(SELECT 1 FROM ingest_batches WHERE id = @p0 AND project_id = @p1)",
                    [("p0", edge.IngestBatchId), ("p1", edge.ProjectId)], ct))
                throw new InvalidOperationException(
                    $"Ingest batch not found for project {edge.ProjectId}: {edge.IngestBatchId}");
        }

        // 5. Clusters reference in-batch record ids; their project exists.
        foreach (var cluster in batch.Clusters)
        {
            if (!await ExistsAsync(conn, tx,
                    "SELECT EXISTS(SELECT 1 FROM projects WHERE id = @p0)",
                    [("p0", cluster.ProjectId)], ct))
                throw new InvalidOperationException($"Project not found: {cluster.ProjectId}");

            if (cluster.MemberEntityRecordIds.Any(id => !recordIds.Contains(id)))
                throw new InvalidOperationException(
                    "Cluster references an entity record outside the completed batch.");
        }

        // 6. Versions reference in-batch golden ids; their batch exists.
        var goldenIds = batch.GoldenRecords.Select(g => g.Id).ToHashSet();
        foreach (var version in batch.GoldenRecordVersions)
        {
            if (!goldenIds.Contains(version.GoldenRecordId))
                throw new InvalidOperationException(
                    "Golden-record version references a golden record outside the completed batch.");

            if (!await ExistsAsync(conn, tx,
                    "SELECT EXISTS(SELECT 1 FROM ingest_batches WHERE id = @p0 AND project_id = @p1)",
                    [("p0", version.IngestBatchId), ("p1", version.ProjectId)], ct))
                throw new InvalidOperationException(
                    $"Ingest batch not found for project {version.ProjectId}: {version.IngestBatchId}");
        }
    }

    private static async Task<bool> ExistsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        string sql, (string Name, object Value)[] parameters, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private void IndexRecords(IReadOnlyCollection<EntityRecord> records)
    {
        if (_index is null || records.Count == 0)
            return;
        foreach (var record in records)
            _index.Index(record);
        _index.Commit();
    }

    // ──────────────────────────────── Validation ────────────────────────────────

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

    // ──────────────────────────────── Row DTOs ──────────────────────────────────

    private sealed class ProjectRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string ContentType { get; set; } = "";
        public string? MergeConfigurationJson { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class SourceRow
    {
        public Guid Id { get; set; }
        public Guid ProjectId { get; set; }
        public string Name { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }

    private sealed class IngestBatchRow
    {
        public Guid Id { get; set; }
        public Guid ProjectId { get; set; }
        public Guid SourceId { get; set; }
        public Guid? JobId { get; set; }
        public int RecordCount { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class EntityRecordRow
    {
        public Guid Id { get; set; }
        public Guid ProjectId { get; set; }
        public Guid SourceId { get; set; }
        public Guid IngestBatchId { get; set; }
        public string SourceRecordId { get; set; } = "";
        public string FieldsJson { get; set; } = "{}";
        public string? BlockingKeysJson { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class ClusterRow
    {
        public Guid Id { get; set; }
        public Guid ProjectId { get; set; }
        public string Status { get; set; } = "active";
        public Guid? MergedIntoClusterId { get; set; }
        public long ComparisonsInside { get; set; }
        public long AgreementsInside { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class ClusterMemberRow
    {
        public Guid ClusterId { get; set; }
        public Guid RecordId { get; set; }
    }

    private sealed class MatchEdgeRow
    {
        public Guid Id { get; set; }
        public Guid ProjectId { get; set; }
        public Guid IngestBatchId { get; set; }
        public Guid LeftEntityRecordId { get; set; }
        public Guid RightEntityRecordId { get; set; }
        public double Score { get; set; }
        public string Method { get; set; } = "";
        public string Decision { get; set; } = "";
        public string BreakdownJson { get; set; } = "[]";
        public string Scorer { get; set; } = "";
        public string ProfileContentType { get; set; } = "";
        public string ProfileFingerprint { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }

    private sealed class GoldenRecordRow
    {
        public Guid Id { get; set; }
        public Guid ProjectId { get; set; }
        public Guid ClusterId { get; set; }
        public Guid CurrentVersionId { get; set; }
        public string FieldsJson { get; set; } = "{}";
        public DateTime UpdatedAt { get; set; }
    }

    private sealed class GoldenRecordVersionRow
    {
        public Guid Id { get; set; }
        public Guid GoldenRecordId { get; set; }
        public Guid ProjectId { get; set; }
        public Guid ClusterId { get; set; }
        public Guid IngestBatchId { get; set; }
        public int VersionNumber { get; set; }
        public string FieldsJson { get; set; } = "{}";
        public DateTime CreatedAt { get; set; }
    }

    private sealed class ReviewTaskRow
    {
        public Guid Id { get; set; }
        public Guid ProjectId { get; set; }
        public Guid IngestBatchId { get; set; }
        public Guid NewEntityRecordId { get; set; }
        public Guid CandidateEntityRecordId { get; set; }
        public double Score { get; set; }
        public string Reason { get; set; } = "";
        public string Status { get; set; } = "";
        public string BreakdownJson { get; set; } = "[]";
        public Guid? LeftClusterId { get; set; }
        public Guid? RightClusterId { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class ClusterMergeEventRow
    {
        public Guid Id { get; set; }
        public Guid ProjectId { get; set; }
        public Guid SurvivorClusterId { get; set; }
        public Guid AbsorbedClusterId { get; set; }
        public string? AbsorbedMemberIdsJson { get; set; }
        public string? TriggerRecordIdsJson { get; set; }
        public double Score { get; set; }
        public string BreakdownJson { get; set; } = "[]";
        public Guid IngestBatchId { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class RecordCorrectedEventRow
    {
        public Guid Id { get; set; }
        public Guid ProjectId { get; set; }
        public Guid SupersededEntityRecordId { get; set; }
        public Guid CorrectedEntityRecordId { get; set; }
        public string PreviousFieldsJson { get; set; } = "{}";
        public string NewFieldsJson { get; set; } = "{}";
        public Guid? PreviousClusterId { get; set; }
        public Guid IngestBatchId { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class RecordDeletedEventRow
    {
        public Guid Id { get; set; }
        public Guid ProjectId { get; set; }
        public Guid DeletedEntityRecordId { get; set; }
        public string PreviousFieldsJson { get; set; } = "{}";
        public Guid? PreviousClusterId { get; set; }
        public Guid IngestBatchId { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    // ──────────────────────────────── Mapping ───────────────────────────────────

    private static Project MapProject(ProjectRow row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        ContentType = row.ContentType,
        MergeConfiguration = row.MergeConfigurationJson is null
            ? null
            : JsonSerializer.Deserialize<MergeConfiguration>(row.MergeConfigurationJson, JsonOpts),
        CreatedAt = new DateTimeOffset(row.CreatedAt, TimeSpan.Zero)
    };

    private static Source MapSource(SourceRow row) => new()
    {
        Id = row.Id,
        ProjectId = row.ProjectId,
        Name = row.Name,
        CreatedAt = new DateTimeOffset(row.CreatedAt, TimeSpan.Zero)
    };

    private static IngestBatch MapIngestBatch(IngestBatchRow row) => new()
    {
        Id = row.Id,
        ProjectId = row.ProjectId,
        SourceId = row.SourceId,
        JobId = row.JobId,
        RecordCount = row.RecordCount,
        CreatedAt = new DateTimeOffset(row.CreatedAt, TimeSpan.Zero)
    };

    private static EntityRecord MapEntityRecord(EntityRecordRow row) => new()
    {
        Id = row.Id,
        ProjectId = row.ProjectId,
        SourceId = row.SourceId,
        IngestBatchId = row.IngestBatchId,
        SourceRecordId = row.SourceRecordId,
        Fields = JsonSerializer.Deserialize<Dictionary<string, string>>(row.FieldsJson, JsonOpts)
                 ?? new Dictionary<string, string>(),
        BlockingKeys = row.BlockingKeysJson is null
            ? []
            : (JsonSerializer.Deserialize<string[]>(row.BlockingKeysJson, JsonOpts) ?? []),
        CreatedAt = new DateTimeOffset(row.CreatedAt, TimeSpan.Zero)
    };

    private static Cluster MapCluster(ClusterRow row, Dictionary<Guid, IReadOnlyList<Guid>> membersByCluster) => new()
    {
        Id = row.Id,
        ProjectId = row.ProjectId,
        Status = row.Status,
        MergedIntoClusterId = row.MergedIntoClusterId,
        ComparisonsInside = row.ComparisonsInside,
        AgreementsInside = row.AgreementsInside,
        MemberEntityRecordIds = membersByCluster.TryGetValue(row.Id, out var members) ? members : [],
        CreatedAt = new DateTimeOffset(row.CreatedAt, TimeSpan.Zero)
    };

    private static MatchEdge MapMatchEdge(MatchEdgeRow row) => new()
    {
        Id = row.Id,
        ProjectId = row.ProjectId,
        IngestBatchId = row.IngestBatchId,
        LeftEntityRecordId = row.LeftEntityRecordId,
        RightEntityRecordId = row.RightEntityRecordId,
        Score = row.Score,
        Method = row.Method,
        Decision = row.Decision,
        Breakdown = JsonSerializer.Deserialize<MatchScoreFactor[]>(row.BreakdownJson, JsonOpts) ?? [],
        Scorer = row.Scorer,
        ProfileContentType = row.ProfileContentType,
        ProfileFingerprint = row.ProfileFingerprint,
        CreatedAt = new DateTimeOffset(row.CreatedAt, TimeSpan.Zero)
    };

    private static GoldenRecord MapGoldenRecord(GoldenRecordRow row) => new()
    {
        Id = row.Id,
        ProjectId = row.ProjectId,
        ClusterId = row.ClusterId,
        CurrentVersionId = row.CurrentVersionId,
        Fields = JsonSerializer.Deserialize<Dictionary<string, string>>(row.FieldsJson, JsonOpts)
                 ?? new Dictionary<string, string>(),
        UpdatedAt = new DateTimeOffset(row.UpdatedAt, TimeSpan.Zero)
    };

    private static GoldenRecordVersion MapGoldenRecordVersion(GoldenRecordVersionRow row) => new()
    {
        Id = row.Id,
        GoldenRecordId = row.GoldenRecordId,
        ProjectId = row.ProjectId,
        ClusterId = row.ClusterId,
        IngestBatchId = row.IngestBatchId,
        VersionNumber = row.VersionNumber,
        Fields = JsonSerializer.Deserialize<Dictionary<string, string>>(row.FieldsJson, JsonOpts)
                 ?? new Dictionary<string, string>(),
        CreatedAt = new DateTimeOffset(row.CreatedAt, TimeSpan.Zero)
    };

    private static ReviewTask MapReviewTask(ReviewTaskRow row) => new()
    {
        Id = row.Id,
        ProjectId = row.ProjectId,
        IngestBatchId = row.IngestBatchId,
        NewEntityRecordId = row.NewEntityRecordId,
        CandidateEntityRecordId = row.CandidateEntityRecordId,
        Score = row.Score,
        Reason = row.Reason,
        Status = row.Status,
        Breakdown = JsonSerializer.Deserialize<MatchScoreFactor[]>(row.BreakdownJson, JsonOpts) ?? [],
        LeftClusterId = row.LeftClusterId,
        RightClusterId = row.RightClusterId,
        CreatedAt = new DateTimeOffset(row.CreatedAt, TimeSpan.Zero)
    };

    private static ClusterMergeEvent MapClusterMergeEvent(ClusterMergeEventRow row) => new()
    {
        Id = row.Id,
        ProjectId = row.ProjectId,
        SurvivorClusterId = row.SurvivorClusterId,
        AbsorbedClusterId = row.AbsorbedClusterId,
        AbsorbedMemberEntityRecordIds = row.AbsorbedMemberIdsJson is null
            ? []
            : (JsonSerializer.Deserialize<Guid[]>(row.AbsorbedMemberIdsJson, JsonOpts) ?? []),
        TriggerRecordIds = row.TriggerRecordIdsJson is null
            ? []
            : (JsonSerializer.Deserialize<Guid[]>(row.TriggerRecordIdsJson, JsonOpts) ?? []),
        Score = row.Score,
        Breakdown = JsonSerializer.Deserialize<MatchScoreFactor[]>(row.BreakdownJson, JsonOpts) ?? [],
        IngestBatchId = row.IngestBatchId,
        CreatedAt = new DateTimeOffset(row.CreatedAt, TimeSpan.Zero)
    };

    private static RecordCorrectedEvent MapRecordCorrectedEvent(RecordCorrectedEventRow row) => new()
    {
        Id = row.Id,
        ProjectId = row.ProjectId,
        SupersededEntityRecordId = row.SupersededEntityRecordId,
        CorrectedEntityRecordId = row.CorrectedEntityRecordId,
        PreviousFields = JsonSerializer.Deserialize<Dictionary<string, string>>(row.PreviousFieldsJson, JsonOpts)
                          ?? new Dictionary<string, string>(),
        NewFields = JsonSerializer.Deserialize<Dictionary<string, string>>(row.NewFieldsJson, JsonOpts)
                    ?? new Dictionary<string, string>(),
        PreviousClusterId = row.PreviousClusterId,
        IngestBatchId = row.IngestBatchId,
        CreatedAt = new DateTimeOffset(row.CreatedAt, TimeSpan.Zero)
    };

    private static RecordDeletedEvent MapRecordDeletedEvent(RecordDeletedEventRow row) => new()
    {
        Id = row.Id,
        ProjectId = row.ProjectId,
        DeletedEntityRecordId = row.DeletedEntityRecordId,
        PreviousFields = JsonSerializer.Deserialize<Dictionary<string, string>>(row.PreviousFieldsJson, JsonOpts)
                          ?? new Dictionary<string, string>(),
        PreviousClusterId = row.PreviousClusterId,
        IngestBatchId = row.IngestBatchId,
        CreatedAt = new DateTimeOffset(row.CreatedAt, TimeSpan.Zero)
    };
}
