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
        // Cluster membership on Postgres is derived from entity_records.cluster_id (the record
        // points to its cluster) — the opposite of the file store's Cluster.MemberEntityRecordIds
        // (the cluster lists its members). DetachFromCluster produces a REDUCED member list for the
        // cluster the departing record left; on the file store that's automatically correct (the old
        // list is replaced). On Postgres, nothing before this milestone ever needed to CLEAR
        // cluster_id for a record that leaves a cluster and joins nothing else — every prior mutation
        // path (merge, dissolution) always moves a departing record onto some new cluster.
        // RecordsToUpdate IS exactly "the record that just became superseded or deleted" (no other
        // code path writes to it), so clearing cluster_id here — in the same statement that writes
        // the tombstone timestamp — is necessary and sufficient: a corrected/deleted record can never
        // appear in a future cluster-membership read, because appearing there requires a non-null
        // cluster_id, and this is the only place cluster_id is ever cleared.
        if (m.RecordsToUpdate.Count > 0)
            await UpdateRecordsAsync(m.RecordsToUpdate, ct);

        if (m.CorrectionEventsToInsert.Count > 0)
            await InsertCorrectionEventsAsync(m.CorrectionEventsToInsert, ct);

        if (m.DeletionEventsToInsert.Count > 0)
            await InsertDeletionEventsAsync(m.DeletionEventsToInsert, ct);

        // Build record→clusterId map from ClustersToUpsert membership.
        var recordToCluster = new Dictionary<Guid, Guid>();
        foreach (var cluster in m.ClustersToUpsert)
            foreach (var memberId in cluster.MemberEntityRecordIds)
                recordToCluster[memberId] = cluster.Id;

        await CopyEntityRecordsAsync(m.RecordsToInsert, recordToCluster, ct);

        await UpsertClustersAsync(m.ClustersToUpsert, ct);

        // Upsert runs BEFORE the clear, deliberately: within one MutationSet, a cluster can both
        // receive a (now-stale) golden record upsert AND get tombstoned by a LATER correction or
        // deletion detaching the rest of its membership in the SAME batch (e.g. correcting or
        // deleting both members of a 2-member cluster — see #67). GoldenRecordsToUpsert entries
        // only exist in-memory in `m` at this point, not yet persisted to golden_records, so
        // running the DELETE before the INSERT/upsert could only remove a row already persisted
        // from a PRIOR call — never one this same call just queued for the very cluster it is also
        // clearing. Running the clear after the upsert makes a same-batch tombstone always win over
        // an earlier same-batch upsert for that cluster id.
        await UpsertGoldenRecordsAsync(m.GoldenRecordsToUpsert, ct);

        if (m.GoldenRecordClusterIdsToClear.Count > 0)
            await ClearGoldenRecordsAsync(m.GoldenRecordClusterIdsToClear, ct);

        await InsertGoldenRecordVersionsAsync(m.VersionsToInsert, ct);

        await InsertMatchEdgesAsync(m.EdgesToInsert, ct);

        foreach (var task in m.ReviewTasksToInsert)
            await InsertReviewTaskAsync(task, ct);

        foreach (var evt in m.MergeEventsToInsert)
            await InsertClusterMergeEventAsync(evt, ct);

        foreach (var evt in m.DissolutionEventsToInsert)
            await InsertClusterDissolutionEventAsync(evt, ct);
    }

    /// <summary>Max VALUES tuples per multi-row INSERT. Keeps total bound parameters well under
    /// Postgres's 65535-parameter limit (worst case here is 10 params/row → ≤10,000).</summary>
    private const int MaxRowsPerInsert = 1000;

    /// <summary>
    /// Bulk-writes each record's own SupersededAt/DeletedAt (exactly one of the two is set per
    /// record, by the resolver's `with` expression) and clears cluster_id in the same statement —
    /// see the comment in ApplyAsync for why clearing cluster_id here is required on Postgres.
    /// Chunked (≤<see cref="MaxRowsPerInsert"/> rows/statement) via UPDATE ... FROM (VALUES ...),
    /// same pattern as RepointActiveClusterMembersAsync. No-op when empty.
    /// </summary>
    private async Task UpdateRecordsAsync(IReadOnlyList<EntityRecord> records, CancellationToken ct)
    {
        for (int offset = 0; offset < records.Count; offset += MaxRowsPerInsert)
        {
            int count = Math.Min(MaxRowsPerInsert, records.Count - offset);
            var sql = new StringBuilder(
                "UPDATE entity_records AS er SET superseded_at = v.sa, deleted_at = v.da, cluster_id = NULL " +
                "FROM (VALUES ");
            await using var cmd = new NpgsqlCommand { Connection = conn, Transaction = tx };
            for (int i = 0; i < count; i++)
            {
                var record = records[offset + i];
                if (i > 0)
                    sql.Append(',');
                sql.Append($"(@id{i}::uuid, @sa{i}::timestamptz, @da{i}::timestamptz)");
                cmd.Parameters.AddWithValue($"id{i}", record.Id);
                cmd.Parameters.Add(new NpgsqlParameter($"sa{i}", NpgsqlDbType.TimestampTz)
                    { Value = record.SupersededAt.HasValue ? (object)record.SupersededAt.Value.UtcDateTime : DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter($"da{i}", NpgsqlDbType.TimestampTz)
                    { Value = record.DeletedAt.HasValue ? (object)record.DeletedAt.Value.UtcDateTime : DBNull.Value });
            }
            sql.Append(") AS v(id, sa, da) WHERE er.id = v.id");
            cmd.CommandText = sql.ToString();
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Inserts all record_corrected_events via chunked multi-row INSERTs (≤<see cref="MaxRowsPerInsert"/>
    /// rows/statement), following InsertClusterDissolutionEventAsync's exact pattern. No-op when empty.
    /// </summary>
    private async Task InsertCorrectionEventsAsync(IReadOnlyList<RecordCorrectedEvent> events, CancellationToken ct)
    {
        for (int offset = 0; offset < events.Count; offset += MaxRowsPerInsert)
        {
            int count = Math.Min(MaxRowsPerInsert, events.Count - offset);
            var sql = new StringBuilder(
                "INSERT INTO record_corrected_events " +
                "(id, project_id, superseded_entity_record_id, corrected_entity_record_id, " +
                "previous_fields, new_fields, previous_cluster_id, ingest_batch_id, created_at) VALUES ");
            await using var cmd = new NpgsqlCommand { Connection = conn, Transaction = tx };
            for (int i = 0; i < count; i++)
            {
                var evt = events[offset + i];
                if (i > 0)
                    sql.Append(',');
                sql.Append($"(@id{i}, @pr{i}, @se{i}, @ce{i}, @pf{i}::jsonb, @nf{i}::jsonb, @pc{i}, @ib{i}, @ca{i})");
                cmd.Parameters.AddWithValue($"id{i}", evt.Id);
                cmd.Parameters.AddWithValue($"pr{i}", evt.ProjectId);
                cmd.Parameters.AddWithValue($"se{i}", evt.SupersededEntityRecordId);
                cmd.Parameters.AddWithValue($"ce{i}", evt.CorrectedEntityRecordId);
                cmd.Parameters.AddWithValue($"pf{i}", JsonSerializer.Serialize(evt.PreviousFields, JsonOpts));
                cmd.Parameters.AddWithValue($"nf{i}", JsonSerializer.Serialize(evt.NewFields, JsonOpts));
                cmd.Parameters.Add(new NpgsqlParameter($"pc{i}", NpgsqlDbType.Uuid)
                    { Value = evt.PreviousClusterId.HasValue ? (object)evt.PreviousClusterId.Value : DBNull.Value });
                cmd.Parameters.AddWithValue($"ib{i}", evt.IngestBatchId);
                cmd.Parameters.AddWithValue($"ca{i}", evt.CreatedAt.UtcDateTime);
            }
            cmd.CommandText = sql.ToString();
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Inserts all record_deleted_events via chunked multi-row INSERTs (≤<see cref="MaxRowsPerInsert"/>
    /// rows/statement), same pattern as InsertCorrectionEventsAsync. No-op when empty.
    /// </summary>
    private async Task InsertDeletionEventsAsync(IReadOnlyList<RecordDeletedEvent> events, CancellationToken ct)
    {
        for (int offset = 0; offset < events.Count; offset += MaxRowsPerInsert)
        {
            int count = Math.Min(MaxRowsPerInsert, events.Count - offset);
            var sql = new StringBuilder(
                "INSERT INTO record_deleted_events " +
                "(id, project_id, deleted_entity_record_id, previous_fields, previous_cluster_id, ingest_batch_id, created_at) VALUES ");
            await using var cmd = new NpgsqlCommand { Connection = conn, Transaction = tx };
            for (int i = 0; i < count; i++)
            {
                var evt = events[offset + i];
                if (i > 0)
                    sql.Append(',');
                sql.Append($"(@id{i}, @pr{i}, @de{i}, @pf{i}::jsonb, @pc{i}, @ib{i}, @ca{i})");
                cmd.Parameters.AddWithValue($"id{i}", evt.Id);
                cmd.Parameters.AddWithValue($"pr{i}", evt.ProjectId);
                cmd.Parameters.AddWithValue($"de{i}", evt.DeletedEntityRecordId);
                cmd.Parameters.AddWithValue($"pf{i}", JsonSerializer.Serialize(evt.PreviousFields, JsonOpts));
                cmd.Parameters.Add(new NpgsqlParameter($"pc{i}", NpgsqlDbType.Uuid)
                    { Value = evt.PreviousClusterId.HasValue ? (object)evt.PreviousClusterId.Value : DBNull.Value });
                cmd.Parameters.AddWithValue($"ib{i}", evt.IngestBatchId);
                cmd.Parameters.AddWithValue($"ca{i}", evt.CreatedAt.UtcDateTime);
            }
            cmd.CommandText = sql.ToString();
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

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
                "INSERT INTO clusters (id, project_id, created_at, status, merged_into_cluster_id, " +
                "comparisons_inside, agreements_inside) VALUES ");
            await using var cmd = new NpgsqlCommand { Connection = conn, Transaction = tx };
            for (int i = 0; i < count; i++)
            {
                var cluster = distinct[offset + i];
                if (i > 0)
                    sql.Append(',');
                sql.Append($"(@id{i}, @pr{i}, @ca{i}, @st{i}, @mi{i}, @ci{i}, @ai{i})");
                cmd.Parameters.AddWithValue($"id{i}", cluster.Id);
                cmd.Parameters.AddWithValue($"pr{i}", cluster.ProjectId);
                cmd.Parameters.AddWithValue($"ca{i}", cluster.CreatedAt.UtcDateTime);
                cmd.Parameters.AddWithValue($"st{i}", cluster.Status);
                cmd.Parameters.Add(new NpgsqlParameter($"mi{i}", NpgsqlDbType.Uuid)
                    { Value = cluster.MergedIntoClusterId.HasValue
                        ? (object)cluster.MergedIntoClusterId.Value
                        : DBNull.Value });
                cmd.Parameters.AddWithValue($"ci{i}", cluster.ComparisonsInside);
                cmd.Parameters.AddWithValue($"ai{i}", cluster.AgreementsInside);
            }
            sql.Append(" ON CONFLICT (id) DO UPDATE SET status = EXCLUDED.status, " +
                       "merged_into_cluster_id = EXCLUDED.merged_into_cluster_id, " +
                       "comparisons_inside = EXCLUDED.comparisons_inside, " +
                       "agreements_inside = EXCLUDED.agreements_inside");
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
                "score, method, decision, breakdown, scorer, profile_content_type, profile_fingerprint, created_at) VALUES ");
            await using var cmd = new NpgsqlCommand { Connection = conn, Transaction = tx };
            for (int i = 0; i < count; i++)
            {
                var edge = edges[offset + i];
                if (i > 0)
                    sql.Append(',');
                sql.Append(
                    $"(@id{i}, @pr{i}, @ib{i}, @l{i}, @r{i}, @sc{i}, @me{i}, @de{i}, @bd{i}::jsonb, @sn{i}, @pc{i}, @pf{i}, @ca{i})");
                cmd.Parameters.AddWithValue($"id{i}", edge.Id);
                cmd.Parameters.AddWithValue($"pr{i}", edge.ProjectId);
                cmd.Parameters.AddWithValue($"ib{i}", edge.IngestBatchId);
                cmd.Parameters.AddWithValue($"l{i}", edge.LeftEntityRecordId);
                cmd.Parameters.AddWithValue($"r{i}", edge.RightEntityRecordId);
                cmd.Parameters.AddWithValue($"sc{i}", edge.Score);
                cmd.Parameters.AddWithValue($"me{i}", edge.Method);
                cmd.Parameters.AddWithValue($"de{i}", edge.Decision);
                cmd.Parameters.AddWithValue($"sn{i}", edge.Scorer);
                cmd.Parameters.AddWithValue($"pc{i}", edge.ProfileContentType);
                cmd.Parameters.AddWithValue($"pf{i}", edge.ProfileFingerprint);
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

    private async Task InsertClusterDissolutionEventAsync(ClusterDissolutionEvent evt, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO cluster_dissolution_events
                (id, project_id, member_entity_record_ids, previous_cluster_id,
                 reason, comparisons_inside, agreements_inside, ingest_batch_id, created_at)
            VALUES
                (@id, @projectId, @memberIds, @previousClusterId,
                 @reason, @comparisonsInside, @agreementsInside, @ingestBatchId, @createdAt)
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", evt.Id);
        cmd.Parameters.AddWithValue("projectId", evt.ProjectId);
        cmd.Parameters.Add(new NpgsqlParameter("memberIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            { Value = evt.MemberEntityRecordIds.ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("previousClusterId", NpgsqlDbType.Uuid)
            { Value = evt.PreviousClusterId.HasValue ? (object)evt.PreviousClusterId.Value : DBNull.Value });
        cmd.Parameters.AddWithValue("reason", evt.Reason);
        cmd.Parameters.AddWithValue("comparisonsInside", evt.ComparisonsInside);
        cmd.Parameters.AddWithValue("agreementsInside", evt.AgreementsInside);
        cmd.Parameters.AddWithValue("ingestBatchId", evt.IngestBatchId);
        cmd.Parameters.AddWithValue("createdAt", evt.CreatedAt.UtcDateTime);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
