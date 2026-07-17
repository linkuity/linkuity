using System.Text;
using System.Text.Json;
using Linkuity.Core.Models;
using Linkuity.Mdm.Resolution;
using Npgsql;
using NpgsqlTypes;

namespace Linkuity.Infrastructure.Postgres;

internal sealed class PostgresMutationApplier(NpgsqlConnection conn, NpgsqlTransaction tx)
{
    private static readonly JsonSerializerOptions JsonOpts = new();

    public async Task ApplyAsync(MutationSet m, CancellationToken ct)
    {
        // Build record→clusterId map from ClustersToUpsert membership.
        var recordToCluster = new Dictionary<Guid, Guid>();
        foreach (var cluster in m.ClustersToUpsert)
            foreach (var memberId in cluster.MemberEntityRecordIds)
                recordToCluster[memberId] = cluster.Id;

        await CopyEntityRecordsAsync(m.RecordsToInsert, recordToCluster, ct);

        await UpsertClustersAsync(m.ClustersToUpsert, ct);

        if (m.GoldenRecordClusterIdsToClear.Count > 0)
            await ClearGoldenRecordsAsync(m.GoldenRecordClusterIdsToClear, ct);

        await UpsertGoldenRecordsAsync(m.GoldenRecordsToUpsert, ct);

        await InsertGoldenRecordVersionsAsync(m.VersionsToInsert, ct);

        await InsertMatchEdgesAsync(m.EdgesToInsert, ct);

        foreach (var task in m.ReviewTasksToInsert)
            await InsertReviewTaskAsync(task, ct);

        foreach (var evt in m.MergeEventsToInsert)
            await InsertClusterMergeEventAsync(evt, ct);
    }

    /// <summary>Max VALUES tuples per multi-row INSERT. Keeps total bound parameters well under
    /// Postgres's 65535-parameter limit (worst case here is 10 params/row → ≤10,000).</summary>
    private const int MaxRowsPerInsert = 1000;

    /// <summary>
    /// Bulk-inserts all new entity_records via a single binary COPY on the open conn/tx.
    /// Incoming records are always new inserts (no ON CONFLICT), so COPY is the correct primitive.
    /// Column order and types mirror the schema and the former per-row INSERT byte-for-byte;
    /// cluster_id comes from <paramref name="recordToCluster"/> (NULL when absent). No-op when empty.
    /// </summary>
    private async Task CopyEntityRecordsAsync(
        IReadOnlyList<EntityRecord> records, Dictionary<Guid, Guid> recordToCluster, CancellationToken ct)
    {
        if (records.Count == 0)
            return;

        await using var writer = await conn.BeginBinaryImportAsync(
            """
            COPY entity_records
                (id, project_id, source_id, ingest_batch_id, source_record_id,
                 fields, blocking_keys, cluster_id, created_at)
            FROM STDIN (FORMAT BINARY)
            """, ct);

        foreach (var record in records)
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(record.Id, NpgsqlDbType.Uuid, ct);
            await writer.WriteAsync(record.ProjectId, NpgsqlDbType.Uuid, ct);
            await writer.WriteAsync(record.SourceId, NpgsqlDbType.Uuid, ct);
            await writer.WriteAsync(record.IngestBatchId, NpgsqlDbType.Uuid, ct);
            await writer.WriteAsync(record.SourceRecordId, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(
                JsonSerializer.Serialize(record.Fields, JsonOpts), NpgsqlDbType.Jsonb, ct);
            await writer.WriteAsync(
                record.BlockingKeys.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text, ct);
            if (recordToCluster.TryGetValue(record.Id, out var clusterId))
                await writer.WriteAsync(clusterId, NpgsqlDbType.Uuid, ct);
            else
                await writer.WriteNullAsync(ct);
            await writer.WriteAsync(record.CreatedAt.UtcDateTime, NpgsqlDbType.TimestampTz, ct);
        }

        await writer.CompleteAsync(ct);
    }

    /// <summary>
    /// Upserts all clusters via chunked multi-row INSERT ... ON CONFLICT (≤<see cref="MaxRowsPerInsert"/>
    /// rows/statement), then repoints active-cluster membership in bulk. Replaces the former per-row
    /// upsert+repoint (which was ~1 round-trip per cluster ≈ ~1 per record — the ingest write hot spot).
    /// Deduplicated by id (last-wins, matching the former sequential-upsert semantics) so a repeated
    /// id cannot trip "ON CONFLICT ... cannot affect row a second time". No-op when empty.
    /// </summary>
    private async Task UpsertClustersAsync(IReadOnlyList<Cluster> clusters, CancellationToken ct)
    {
        if (clusters.Count == 0)
            return;

        // Dedupe by id, keeping the last occurrence (equivalent to the prior per-row sequential upsert).
        var byId = new Dictionary<Guid, Cluster>();
        foreach (var cluster in clusters)
            byId[cluster.Id] = cluster;
        var distinct = byId.Values.ToList();

        for (int offset = 0; offset < distinct.Count; offset += MaxRowsPerInsert)
        {
            int count = Math.Min(MaxRowsPerInsert, distinct.Count - offset);
            var sql = new StringBuilder(
                "INSERT INTO clusters (id, project_id, created_at, status, merged_into_cluster_id) VALUES ");
            await using var cmd = new NpgsqlCommand { Connection = conn, Transaction = tx };
            for (int i = 0; i < count; i++)
            {
                var cluster = distinct[offset + i];
                if (i > 0)
                    sql.Append(',');
                sql.Append($"(@id{i}, @pr{i}, @ca{i}, @st{i}, @mi{i})");
                cmd.Parameters.AddWithValue($"id{i}", cluster.Id);
                cmd.Parameters.AddWithValue($"pr{i}", cluster.ProjectId);
                cmd.Parameters.AddWithValue($"ca{i}", cluster.CreatedAt.UtcDateTime);
                cmd.Parameters.AddWithValue($"st{i}", cluster.Status);
                cmd.Parameters.Add(new NpgsqlParameter($"mi{i}", NpgsqlDbType.Uuid)
                    { Value = cluster.MergedIntoClusterId.HasValue
                        ? (object)cluster.MergedIntoClusterId.Value
                        : DBNull.Value });
            }
            sql.Append(" ON CONFLICT (id) DO UPDATE SET status = EXCLUDED.status, " +
                       "merged_into_cluster_id = EXCLUDED.merged_into_cluster_id");
            cmd.CommandText = sql.ToString();
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await RepointActiveClusterMembersAsync(distinct, ct);
    }

    /// <summary>
    /// Bulk-repoints entity_records.cluster_id for the members of ACTIVE clusters via chunked
    /// UPDATE ... FROM (VALUES ...). Membership on Postgres is derived from the single-valued
    /// entity_records.cluster_id, so a merge must move absorbed members onto the survivor (an active
    /// cluster carrying them in this set). Tombstoned (merged) clusters are skipped — their pre-merge
    /// lineage is preserved in cluster_merge_events. Each record belongs to exactly one active cluster
    /// here, so the VALUES set has no duplicate target rows. No-op when empty.
    /// </summary>
    private async Task RepointActiveClusterMembersAsync(IReadOnlyList<Cluster> clusters, CancellationToken ct)
    {
        // Dedupe by member id (last-wins) so a member that (defensively) appeared under two active
        // clusters maps to exactly one target row in the VALUES set — matching the deterministic
        // last-writer-wins of the former per-cluster sequential UPDATE, and the id-dedup already
        // applied to clusters/goldens. In the normal case (each record in one active cluster) this
        // is a no-op.
        var byMember = new Dictionary<Guid, Guid>();
        foreach (var cluster in clusters)
        {
            if (string.Equals(cluster.Status, "merged", StringComparison.Ordinal))
                continue;
            foreach (var memberId in cluster.MemberEntityRecordIds)
                byMember[memberId] = cluster.Id;
        }
        var pairs = byMember.Select(kv => (MemberId: kv.Key, ClusterId: kv.Value)).ToList();

        for (int offset = 0; offset < pairs.Count; offset += MaxRowsPerInsert)
        {
            int count = Math.Min(MaxRowsPerInsert, pairs.Count - offset);
            var sql = new StringBuilder("UPDATE entity_records AS er SET cluster_id = v.cid FROM (VALUES ");
            await using var cmd = new NpgsqlCommand { Connection = conn, Transaction = tx };
            for (int i = 0; i < count; i++)
            {
                var (memberId, clusterId) = pairs[offset + i];
                if (i > 0)
                    sql.Append(',');
                sql.Append($"(@r{i}::uuid, @c{i}::uuid)");
                cmd.Parameters.AddWithValue($"r{i}", memberId);
                cmd.Parameters.AddWithValue($"c{i}", clusterId);
            }
            sql.Append(") AS v(rid, cid) WHERE er.id = v.rid");
            cmd.CommandText = sql.ToString();
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task ClearGoldenRecordsAsync(IReadOnlyList<Guid> clusterIds, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM golden_records WHERE cluster_id = ANY(@ids)",
            conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            { Value = clusterIds.ToArray() });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Upserts all golden_records via chunked multi-row INSERT ... ON CONFLICT (≤<see cref="MaxRowsPerInsert"/>
    /// rows/statement). Same columns, jsonb serialization, and ON CONFLICT semantics as the former
    /// per-row upsert (≈ ~1 round-trip per record — batched here to remove the hot spot). Deduplicated
    /// by id (last-wins) so a repeated id cannot trip the ON CONFLICT self-conflict. No-op when empty.
    /// </summary>
    private async Task UpsertGoldenRecordsAsync(IReadOnlyList<GoldenRecord> goldens, CancellationToken ct)
    {
        if (goldens.Count == 0)
            return;

        var byId = new Dictionary<Guid, GoldenRecord>();
        foreach (var golden in goldens)
            byId[golden.Id] = golden;
        var distinct = byId.Values.ToList();

        for (int offset = 0; offset < distinct.Count; offset += MaxRowsPerInsert)
        {
            int count = Math.Min(MaxRowsPerInsert, distinct.Count - offset);
            var sql = new StringBuilder(
                "INSERT INTO golden_records " +
                "(id, project_id, cluster_id, current_version_id, fields, updated_at) VALUES ");
            await using var cmd = new NpgsqlCommand { Connection = conn, Transaction = tx };
            for (int i = 0; i < count; i++)
            {
                var golden = distinct[offset + i];
                if (i > 0)
                    sql.Append(',');
                sql.Append($"(@id{i}, @pr{i}, @cl{i}, @cv{i}, @f{i}::jsonb, @ua{i})");
                cmd.Parameters.AddWithValue($"id{i}", golden.Id);
                cmd.Parameters.AddWithValue($"pr{i}", golden.ProjectId);
                cmd.Parameters.AddWithValue($"cl{i}", golden.ClusterId);
                cmd.Parameters.AddWithValue($"cv{i}", golden.CurrentVersionId);
                cmd.Parameters.AddWithValue($"f{i}", JsonSerializer.Serialize(golden.Fields, JsonOpts));
                cmd.Parameters.AddWithValue($"ua{i}", golden.UpdatedAt.UtcDateTime);
            }
            sql.Append(" ON CONFLICT (id) DO UPDATE SET " +
                       "cluster_id = EXCLUDED.cluster_id, current_version_id = EXCLUDED.current_version_id, " +
                       "fields = EXCLUDED.fields, updated_at = EXCLUDED.updated_at");
            cmd.CommandText = sql.ToString();
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Inserts all golden_record_versions via chunked multi-row INSERTs (≤<see cref="MaxRowsPerInsert"/>
    /// rows/statement). Same columns, jsonb serialization, and values as the former per-row insert.
    /// No-op when empty.
    /// </summary>
    private async Task InsertGoldenRecordVersionsAsync(
        IReadOnlyList<GoldenRecordVersion> versions, CancellationToken ct)
    {
        for (int offset = 0; offset < versions.Count; offset += MaxRowsPerInsert)
        {
            int count = Math.Min(MaxRowsPerInsert, versions.Count - offset);
            var sql = new StringBuilder(
                "INSERT INTO golden_record_versions " +
                "(id, golden_record_id, project_id, cluster_id, ingest_batch_id, " +
                "version_number, fields, created_at) VALUES ");
            await using var cmd = new NpgsqlCommand { Connection = conn, Transaction = tx };
            for (int i = 0; i < count; i++)
            {
                var version = versions[offset + i];
                if (i > 0)
                    sql.Append(',');
                sql.Append($"(@id{i}, @gr{i}, @pr{i}, @cl{i}, @ib{i}, @vn{i}, @f{i}::jsonb, @ca{i})");
                cmd.Parameters.AddWithValue($"id{i}", version.Id);
                cmd.Parameters.AddWithValue($"gr{i}", version.GoldenRecordId);
                cmd.Parameters.AddWithValue($"pr{i}", version.ProjectId);
                cmd.Parameters.AddWithValue($"cl{i}", version.ClusterId);
                cmd.Parameters.AddWithValue($"ib{i}", version.IngestBatchId);
                cmd.Parameters.AddWithValue($"vn{i}", version.VersionNumber);
                cmd.Parameters.AddWithValue($"f{i}", JsonSerializer.Serialize(version.Fields, JsonOpts));
                cmd.Parameters.AddWithValue($"ca{i}", version.CreatedAt.UtcDateTime);
            }
            cmd.CommandText = sql.ToString();
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Inserts all match_edges via chunked multi-row INSERTs (≤<see cref="MaxRowsPerInsert"/>
    /// rows/statement). Same columns, jsonb breakdown serialization, and values as the former
    /// per-row insert. No-op when empty.
    /// </summary>
    private async Task InsertMatchEdgesAsync(IReadOnlyList<MatchEdge> edges, CancellationToken ct)
    {
        for (int offset = 0; offset < edges.Count; offset += MaxRowsPerInsert)
        {
            int count = Math.Min(MaxRowsPerInsert, edges.Count - offset);
            var sql = new StringBuilder(
                "INSERT INTO match_edges " +
                "(id, project_id, ingest_batch_id, left_entity_record_id, right_entity_record_id, " +
                "score, method, decision, breakdown, created_at) VALUES ");
            await using var cmd = new NpgsqlCommand { Connection = conn, Transaction = tx };
            for (int i = 0; i < count; i++)
            {
                var edge = edges[offset + i];
                if (i > 0)
                    sql.Append(',');
                sql.Append(
                    $"(@id{i}, @pr{i}, @ib{i}, @l{i}, @r{i}, @sc{i}, @me{i}, @de{i}, @bd{i}::jsonb, @ca{i})");
                cmd.Parameters.AddWithValue($"id{i}", edge.Id);
                cmd.Parameters.AddWithValue($"pr{i}", edge.ProjectId);
                cmd.Parameters.AddWithValue($"ib{i}", edge.IngestBatchId);
                cmd.Parameters.AddWithValue($"l{i}", edge.LeftEntityRecordId);
                cmd.Parameters.AddWithValue($"r{i}", edge.RightEntityRecordId);
                cmd.Parameters.AddWithValue($"sc{i}", edge.Score);
                cmd.Parameters.AddWithValue($"me{i}", edge.Method);
                cmd.Parameters.AddWithValue($"de{i}", edge.Decision);
                cmd.Parameters.AddWithValue($"bd{i}", JsonSerializer.Serialize(edge.Breakdown, JsonOpts));
                cmd.Parameters.AddWithValue($"ca{i}", edge.CreatedAt.UtcDateTime);
            }
            cmd.CommandText = sql.ToString();
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task InsertReviewTaskAsync(ReviewTask task, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO review_tasks
                (id, project_id, ingest_batch_id, new_entity_record_id, candidate_entity_record_id,
                 score, reason, status, breakdown, left_cluster_id, right_cluster_id, created_at)
            VALUES
                (@id, @projectId, @ingestBatchId, @newRecordId, @candidateRecordId,
                 @score, @reason, @status, @breakdown::jsonb, @leftClusterId, @rightClusterId, @createdAt)
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", task.Id);
        cmd.Parameters.AddWithValue("projectId", task.ProjectId);
        cmd.Parameters.AddWithValue("ingestBatchId", task.IngestBatchId);
        cmd.Parameters.AddWithValue("newRecordId", task.NewEntityRecordId);
        cmd.Parameters.AddWithValue("candidateRecordId", task.CandidateEntityRecordId);
        cmd.Parameters.AddWithValue("score", task.Score);
        cmd.Parameters.AddWithValue("reason", task.Reason);
        cmd.Parameters.AddWithValue("status", task.Status);
        cmd.Parameters.AddWithValue("breakdown", JsonSerializer.Serialize(task.Breakdown, JsonOpts));
        cmd.Parameters.Add(new NpgsqlParameter("leftClusterId", NpgsqlDbType.Uuid)
            { Value = task.LeftClusterId.HasValue ? (object)task.LeftClusterId.Value : DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("rightClusterId", NpgsqlDbType.Uuid)
            { Value = task.RightClusterId.HasValue ? (object)task.RightClusterId.Value : DBNull.Value });
        cmd.Parameters.AddWithValue("createdAt", task.CreatedAt.UtcDateTime);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task InsertClusterMergeEventAsync(ClusterMergeEvent evt, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO cluster_merge_events
                (id, project_id, survivor_cluster_id, absorbed_cluster_id,
                 absorbed_member_entity_record_ids, trigger_record_ids,
                 score, breakdown, ingest_batch_id, created_at)
            VALUES
                (@id, @projectId, @survivorId, @absorbedId,
                 @absorbedMemberIds, @triggerIds,
                 @score, @breakdown::jsonb, @ingestBatchId, @createdAt)
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", evt.Id);
        cmd.Parameters.AddWithValue("projectId", evt.ProjectId);
        cmd.Parameters.AddWithValue("survivorId", evt.SurvivorClusterId);
        cmd.Parameters.AddWithValue("absorbedId", evt.AbsorbedClusterId);
        cmd.Parameters.Add(new NpgsqlParameter("absorbedMemberIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            { Value = evt.AbsorbedMemberEntityRecordIds.ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("triggerIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            { Value = evt.TriggerRecordIds.ToArray() });
        cmd.Parameters.AddWithValue("score", evt.Score);
        cmd.Parameters.AddWithValue("breakdown", JsonSerializer.Serialize(evt.Breakdown, JsonOpts));
        cmd.Parameters.AddWithValue("ingestBatchId", evt.IngestBatchId);
        cmd.Parameters.AddWithValue("createdAt", evt.CreatedAt.UtcDateTime);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
