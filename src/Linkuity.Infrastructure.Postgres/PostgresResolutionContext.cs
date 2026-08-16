using System.Text.Json;
using Dapper;
using Linkuity.Core.Models;
using Linkuity.Mdm.Resolution;
using Npgsql;

namespace Linkuity.Infrastructure.Postgres;

/// <summary>
/// Bounded <see cref="IResolutionContext"/> over a single <see cref="NpgsqlConnection"/> +
/// <see cref="NpgsqlTransaction"/>. Every read except <see cref="GetLinearCorpus"/> is bounded by
/// candidate/cluster fan-out (<c>id = ANY(...)</c> / <c>cluster_id = ANY(...)</c>). Every
/// production Postgres deployment attaches a Lucene index (see
/// <c>PostgresInfrastructureServiceCollectionExtensions</c>), so the resolver never requests the
/// linear corpus on that path. <see cref="GetLinearCorpus"/> is still a project-scoped scan (not
/// unbounded across all projects) for the one case that legitimately needs it without an index:
/// F6 milestone 3 corrections/deletion are guarded off on an indexed store (Lucene staleness,
/// deferred to #66), so a store built without one — used directly in that milestone's own
/// integration tests, never by the DI-wired production path above — falls back to this scan,
/// mirroring <c>FileMetadataStore.FileResolutionContext.GetLinearCorpus</c>'s identical filter.
/// </summary>
internal sealed class PostgresResolutionContext(NpgsqlConnection conn, NpgsqlTransaction tx) : IResolutionContext
{
    private static readonly JsonSerializerOptions JsonOpts = new();

    public IReadOnlyList<EntityRecord> GetLinearCorpus(Guid projectId)
    {
        var rows = conn.Query<EntityRecordRow>(new CommandDefinition(
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
            WHERE project_id = @p AND superseded_at IS NULL AND deleted_at IS NULL
            """,
            new { p = projectId }, transaction: tx));

        return rows.Select(MapEntityRecord).ToList();
    }

    public EntityRecord? FindCurrentRecordBySourceRecordId(Guid projectId, string sourceRecordId)
    {
        var rows = conn.Query<EntityRecordRow>(new CommandDefinition(
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
            WHERE project_id = @p AND superseded_at IS NULL AND deleted_at IS NULL
              AND lower(source_record_id) = lower(@srid)
            LIMIT 1
            """,
            new { p = projectId, srid = sourceRecordId }, transaction: tx));

        return rows.Select(MapEntityRecord).FirstOrDefault();
    }

    public IReadOnlyList<Cluster> GetActiveClustersContaining(Guid projectId, IReadOnlyCollection<Guid> recordIds)
    {
        if (recordIds.Count == 0)
            return [];

        var clusterRows = conn.Query<ClusterRow>(new CommandDefinition(
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
            WHERE project_id = @p
              AND status <> 'merged'
              AND id IN (SELECT cluster_id FROM entity_records WHERE id = ANY(@recordIds))
            """,
            new { p = projectId, recordIds = recordIds.ToArray() }, transaction: tx)).ToList();

        if (clusterRows.Count == 0)
            return [];

        // Hydrate each cluster's FULL membership — bounded by cluster fan-out (cluster_id = ANY).
        var clusterIds = clusterRows.Select(r => r.Id).ToArray();
        var memberRows = conn.Query<ClusterMemberRow>(new CommandDefinition(
            """
            SELECT cluster_id AS "ClusterId", id AS "RecordId"
            FROM entity_records
            WHERE cluster_id = ANY(@clusterIds)
            """,
            new { clusterIds }, transaction: tx)).ToList();

        var membersByCluster = memberRows
            .GroupBy(m => m.ClusterId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(m => m.RecordId).ToList());

        return clusterRows.Select(r => new Cluster
        {
            Id = r.Id,
            ProjectId = r.ProjectId,
            Status = r.Status,
            MergedIntoClusterId = r.MergedIntoClusterId,
            ComparisonsInside = r.ComparisonsInside,
            AgreementsInside = r.AgreementsInside,
            MemberEntityRecordIds = membersByCluster.TryGetValue(r.Id, out var members) ? members : [],
            CreatedAt = new DateTimeOffset(r.CreatedAt, TimeSpan.Zero)
        }).ToList();
    }

    public IReadOnlyList<EntityRecord> GetRecordsByIds(Guid projectId, IReadOnlyCollection<Guid> recordIds)
    {
        if (recordIds.Count == 0)
            return [];

        var rows = conn.Query<EntityRecordRow>(new CommandDefinition(
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
            WHERE project_id = @p AND id = ANY(@ids) AND superseded_at IS NULL AND deleted_at IS NULL
            """,
            new { p = projectId, ids = recordIds.ToArray() }, transaction: tx));

        return rows.Select(MapEntityRecord).ToList();
    }

    public IReadOnlyList<GoldenRecord> GetGoldenRecordsForClusters(Guid projectId, IReadOnlyCollection<Guid> clusterIds)
    {
        if (clusterIds.Count == 0)
            return [];

        var rows = conn.Query<GoldenRecordRow>(new CommandDefinition(
            """
            SELECT
                id                 AS "Id",
                project_id         AS "ProjectId",
                cluster_id         AS "ClusterId",
                current_version_id AS "CurrentVersionId",
                fields::text       AS "FieldsJson",
                updated_at         AS "UpdatedAt"
            FROM golden_records
            WHERE project_id = @p AND cluster_id = ANY(@ids)
            """,
            new { p = projectId, ids = clusterIds.ToArray() }, transaction: tx));

        return rows.Select(MapGoldenRecord).ToList();
    }

    public IReadOnlyList<GoldenRecordVersion> GetVersionsForGoldenRecords(IReadOnlyCollection<Guid> goldenRecordIds)
    {
        if (goldenRecordIds.Count == 0)
            return [];

        var rows = conn.Query<GoldenRecordVersionRow>(new CommandDefinition(
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
            WHERE golden_record_id = ANY(@ids)
            """,
            new { ids = goldenRecordIds.ToArray() }, transaction: tx));

        return rows.Select(MapGoldenRecordVersion).ToList();
    }

    // ──────────────────────────────── Mapping ───────────────────────────────────

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

    // ──────────────────────────────── Row DTOs ──────────────────────────────────

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
}
