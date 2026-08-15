using Linkuity.Core.Merge;
using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Clustering;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;
using Linkuity.Matching.Strategies.Defaults;

namespace Linkuity.Mdm.Resolution;

/// <summary>
/// The persistence-agnostic Milestone 22 incremental-ingest algorithm, extracted verbatim
/// from <c>FileMetadataStore.SaveIncrementalIngestAsync</c>. It reads bounded state through
/// <see cref="IResolutionContext"/>, mutates a local <see cref="ResolutionWorkingSet"/>, and
/// returns the targeted <see cref="MutationSet"/> the backend applies in its own transaction.
/// </summary>
public sealed class IncrementalResolver
{
    // The default-similarity strategy scores SHARED BLOCKING KEYS (see DefaultSimilarityStrategy),
    // but a Lucene candidate is a scoring projection with EMPTY BlockingKeys (Milestone 26) — see
    // the guard in Resolve below.
    private static readonly string DefaultSimilarityStrategyName = new DefaultSimilarityStrategy().Name;

    private readonly IMatchingEngine _engine;
    private readonly bool _hasIndex;
    private readonly IClusterMergePolicy _mergePolicy;
    private readonly int _degreeOfParallelism;

    // clusterMergePolicy is required, not defaulted: a resolver built with no policy would silently
    // accept every cluster regardless of its own comparisons, which is the exact defect Task 10
    // exists to close. Making the caller supply one keeps that impossible to do by accident.
    public IncrementalResolver(IMatchingEngine engine, bool hasIndex, IClusterMergePolicy clusterMergePolicy, int degreeOfParallelism = 1)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(clusterMergePolicy);
        _engine = engine;
        _hasIndex = hasIndex;
        _mergePolicy = clusterMergePolicy;
        _degreeOfParallelism = Math.Max(1, degreeOfParallelism);
    }

    /// <summary>
    /// The request's thresholds, validated. Both metadata stores checked these inline with
    /// identical copied code, which is the shape every drifted duplicate in this codebase has
    /// started as. Rules live in <see cref="MatchThresholds"/>; this only restates the failure
    /// against the parameter the caller actually passed.
    /// <para>
    /// <paramref name="scale"/> defaults to <see cref="ScoreScale.UnitInterval"/> because that is
    /// the scale every scorer shipped before "evidence" produces — a caller that has not yet
    /// resolved the profile's own scorer (or does not need to) gets the same behaviour this method
    /// always had. A caller that HAS resolved the profile (both metadata stores, once they have
    /// loaded it) must pass the resolved scale explicitly: validating an evidence profile's
    /// unbounded thresholds against the default would reject every valid one.
    /// </para>
    /// </summary>
    public static MatchThresholds ThresholdsFor(IncrementalIngestRequest request, ScoreScale scale = ScoreScale.UnitInterval)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return new MatchThresholds(request.AutoMatchThreshold, request.ReviewThreshold, scale);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(ex.Message, nameof(request), ex);
        }
    }

    public IReadOnlyList<string> GenerateBlockingKeys(EntityRecord record, MatchingProfile profile)
        => _engine.GenerateBlockingKeys(record, profile);

    /// <summary>
    /// Normalizes field values and derives blocking keys from them, in that order. Durable stores
    /// call this on every incoming record so ingest normalization cannot be skipped by one backend
    /// and applied by another.
    /// </summary>
    public EntityRecord PrepareForStorage(EntityRecord record, MatchingProfile profile)
        => _engine.PrepareForStorage(record, profile);

    public (IReadOnlyList<EntityRecord> RecordsToResolve, MutationSet DetachMutations) ClassifyAndDetachCorrections(
        Project project,
        MatchingProfile profile,
        IReadOnlyList<EntityRecord> incomingRecords,
        IResolutionContext context,
        DateTimeOffset now)
    {
        var mutations = new MutationSet();
        var toResolve = new List<EntityRecord>();
        // Tracks each touched cluster's membership AS REDUCED BY THIS CALL SO FAR — a second
        // correction in the same batch that targets a sibling in the SAME cluster must see the
        // first correction's already-reduced membership, not re-derive "cluster minus my one
        // member" from context's original, undetached state (which would silently drop only the
        // LAST record processed instead of both).
        var pendingMemberIdsByClusterId = new Dictionary<Guid, IReadOnlyList<Guid>>();

        foreach (var incoming in incomingRecords)
        {
            var current = context.FindCurrentRecordBySourceRecordId(incoming.ProjectId, incoming.SourceRecordId);
            if (current is null)
            {
                toResolve.Add(incoming);
                continue;
            }

            if (GoldenRecordMerge.DictionaryEquals(current.Fields, incoming.Fields))
                continue; // identical resend — safe no-op, nothing to do

            // Correction. Detach from any current cluster before it re-enters resolution. The
            // recomputed golden record's version is attributed to the CORRECTING record's own
            // ingest batch (incoming.IngestBatchId) — not the superseded record's original batch —
            // matching how every other golden-record-version write in this file attributes to the
            // triggering call, not the member's own history.
            Guid? previousClusterId = DetachFromCluster(
                project, profile, current, incoming.IngestBatchId, context, mutations, now, pendingMemberIdsByClusterId);

            mutations.RecordsToUpdate.Add(current with { SupersededAt = now });
            mutations.CorrectionEventsToInsert.Add(new RecordCorrectedEvent
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                SupersededEntityRecordId = current.Id,
                CorrectedEntityRecordId = incoming.Id,
                PreviousFields = current.Fields,
                NewFields = incoming.Fields,
                PreviousClusterId = previousClusterId,
                IngestBatchId = incoming.IngestBatchId,
                CreatedAt = now
            });
            toResolve.Add(incoming);
        }

        return (toResolve, mutations);
    }

    // Returns the cluster the record was detached from, or null if it had none (already a
    // singleton). Populates `mutations` with whatever the detach requires — reduced/tombstoned
    // cluster, recomputed golden record — for the CALLER'S old cluster; the record ITSELF re-enters
    // resolution fresh via `toResolve`, handled by the caller. `correctingIngestBatchId` is the
    // NEW (correcting) record's batch — the recomputed golden record's version is attributed to
    // it, not to `record`'s (the superseded record's) own original batch. `pendingMemberIdsByClusterId`
    // carries this SAME CALL's already-reduced membership for a cluster forward across iterations,
    // so a second correction landing on the same cluster detaches from what the first correction
    // left behind, not from context's original, undetached membership.
    private static Guid? DetachFromCluster(
        Project project, MatchingProfile profile, EntityRecord record, Guid correctingIngestBatchId,
        IResolutionContext context, MutationSet mutations, DateTimeOffset now,
        Dictionary<Guid, IReadOnlyList<Guid>> pendingMemberIdsByClusterId)
    {
        var clusters = context.GetActiveClustersContaining(project.Id, [record.Id]);
        if (clusters.Count == 0)
            return null;

        var cluster = clusters[0]; // a record belongs to at most one active cluster
        var currentMemberIds = pendingMemberIdsByClusterId.TryGetValue(cluster.Id, out var pending)
            ? pending
            : cluster.MemberEntityRecordIds;
        var survivorIds = currentMemberIds.Where(id => id != record.Id).ToList();
        pendingMemberIdsByClusterId[cluster.Id] = survivorIds;

        if (survivorIds.Count == 0)
        {
            // The corrected record was the cluster's only member — tombstone it, matching the
            // existing dissolution-tombstone shape (Status = "merged", MergedIntoClusterId = null),
            // and remove its golden record entirely.
            mutations.ClustersToUpsert.Add(new Cluster
            {
                Id = cluster.Id, ProjectId = cluster.ProjectId, MemberEntityRecordIds = cluster.MemberEntityRecordIds,
                CreatedAt = cluster.CreatedAt, Status = "merged", MergedIntoClusterId = null,
                ComparisonsInside = cluster.ComparisonsInside, AgreementsInside = cluster.AgreementsInside
            });
            mutations.GoldenRecordClusterIdsToClear.Add(cluster.Id);
            return cluster.Id;
        }

        // Survivors keep the existing cluster id (F28) — including when exactly one remains and it
        // becomes a de facto singleton; nothing about ITS identity changed.
        mutations.ClustersToUpsert.Add(new Cluster
        {
            Id = cluster.Id, ProjectId = cluster.ProjectId, MemberEntityRecordIds = survivorIds,
            CreatedAt = cluster.CreatedAt, Status = cluster.Status, MergedIntoClusterId = cluster.MergedIntoClusterId,
            ComparisonsInside = cluster.ComparisonsInside, AgreementsInside = cluster.AgreementsInside
        });

        var survivorRecords = context.GetRecordsByIds(project.Id, survivorIds).Select(r => r.Fields).ToList();
        var golden = context.GetGoldenRecordsForClusters(project.Id, [cluster.Id]).FirstOrDefault();
        if (golden is null)
            return cluster.Id; // no golden record existed yet for this cluster — nothing to recompute

        var mergeIndex = project.MergeConfiguration?.MergeFields
            .ToDictionary(f => f.FieldName, f => f.SourcePriority, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var sourceField = profile.Fields
            .FirstOrDefault(f => f.SemanticType == SemanticFieldType.SourceIdentifier)?.Name ?? "source";
        var recomputedFields = GoldenRecordMerge.MergeFields(survivorRecords, mergeIndex, sourceField);

        // Next sequential version number, mirroring UpdateGoldenRecords's own
        // `ws.GoldenRecordVersions.Count(v => v.GoldenRecordId == golden.Id) + 1` — a hardcoded 1
        // would silently overwrite/duplicate an already-versioned golden record's history.
        var existingVersions = context.GetVersionsForGoldenRecords([golden.Id]);

        var versionId = Guid.NewGuid();
        mutations.GoldenRecordsToUpsert.Add(new GoldenRecord
        {
            Id = golden.Id, ProjectId = golden.ProjectId, ClusterId = golden.ClusterId,
            CurrentVersionId = versionId, Fields = recomputedFields, UpdatedAt = now
        });
        mutations.VersionsToInsert.Add(new GoldenRecordVersion
        {
            Id = versionId, GoldenRecordId = golden.Id, ProjectId = golden.ProjectId, ClusterId = golden.ClusterId,
            IngestBatchId = correctingIngestBatchId, VersionNumber = existingVersions.Count + 1,
            Fields = recomputedFields, CreatedAt = now
        });

        return cluster.Id;
    }

    // incomingRecords MUST already carry blocking keys. Returns counts + the targeted mutations to apply.
    public (IncrementalIngestResult Result, MutationSet Mutations) Resolve(
        IncrementalIngestRequest request,
        Project project,
        MatchingProfile profile,
        IReadOnlyList<EntityRecord> incomingRecords,
        IResolutionContext context,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(incomingRecords);
        ArgumentNullException.ThrowIfNull(context);

        var retrievalStrategy = _hasIndex ? "lucene" : "blocking-linear";
        if (_hasIndex && string.Equals(profile.SimilarityStrategy, DefaultSimilarityStrategyName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Matching profile '{profile.ContentType}' declares similarity strategy '{DefaultSimilarityStrategyName}', " +
                "which is incompatible with index-backed retrieval. The default similarity strategy scores shared " +
                "blocking keys, but index-backed (Lucene) retrieval returns a scoring projection without blocking " +
                "keys (Milestone 26), so those matches would silently score 0. Use 'field-weighted' similarity " +
                "with an index, or run without an index.");
        }
        var callProfile = WithCallOverrides(profile, retrievalStrategy, request.AutoMatchThreshold, request.ReviewThreshold);
        var batchCallProfile = WithCallOverrides(profile, "blocking-linear", request.AutoMatchThreshold, request.ReviewThreshold);

        var existingRecords = _hasIndex
            ? Array.Empty<EntityRecord>()
            : context.GetLinearCorpus(request.ProjectId);

        // [C1] Mirrors CorpusAuditService's identical gate (see its own mergePolicyCanReject):
        // whether the injected policy could reject ANY cluster under this profile. Every shipped
        // profile has MinClusterCohesion and MaxAutoClusterSize both null in stage 1a, so this is
        // false today. When false, BuildResolutionEdges below does not retain sub-review
        // comparisons at all, and the cohesion tallying downstream is skipped, so counters stay
        // 0/0 rather than silently reset — cohesion enforcement turning on for an already-ingested
        // project requires re-ingest, which is the documented migration path (see
        // MatchingProfile.MinClusterCohesion), not something this method decides on its own later.
        var mergePolicyCanReject = _mergePolicy.CanReject(profile);

        var (edges, allComparisons) = BuildResolutionEdges(
            incomingRecords, existingRecords, callProfile, batchCallProfile, request, mergePolicyCanReject);

        var incomingIds = incomingRecords.Select(r => r.Id).ToHashSet();
        var touchedExistingIds = edges
            .SelectMany(e => new[] { e.LeftId, e.RightId })
            .Where(id => !incomingIds.Contains(id))
            .ToHashSet();
        var touchedClusters = context.GetActiveClustersContaining(request.ProjectId, touchedExistingIds).ToList();

        // Build and seed a bounded working set (replaces the full-database reads in the source).
        var ws = new ResolutionWorkingSet();
        ws.Clusters.AddRange(touchedClusters);

        var touchedMemberIds = touchedClusters
            .SelectMany(c => c.MemberEntityRecordIds)
            .Distinct()
            .ToList();
        ws.EntityRecords.AddRange(context.GetRecordsByIds(request.ProjectId, touchedMemberIds));
        // Add incoming records before materialization so golden recompute sees them (source :252).
        ws.EntityRecords.AddRange(incomingRecords);

        var touchedClusterIds = touchedClusters.Select(c => c.Id).ToList();
        ws.GoldenRecords.AddRange(context.GetGoldenRecordsForClusters(request.ProjectId, touchedClusterIds));
        var touchedGoldenIds = ws.GoldenRecords.Select(g => g.Id).ToList();
        ws.GoldenRecordVersions.AddRange(context.GetVersionsForGoldenRecords(touchedGoldenIds));

        // Snapshot the seed so we can derive losers (cleared goldens) and net-new versions afterwards.
        var seededGoldenClusterIds = ws.GoldenRecords.Select(g => g.ClusterId).ToHashSet();
        var seededVersionIds = ws.GoldenRecordVersions.Select(v => v.Id).ToHashSet();

        var components = ResolveComponents(incomingRecords, touchedClusters, edges);

        // [C1] Single O(allComparisons) pass replacing the per-component rescan MaterializeComponent
        // used to do (O(components x allComparisons), quadratic on a large single-batch ingest).
        // recordToComponentIndex is built once so a comparison's endpoints resolve to a component
        // in O(1); componentTally is then read O(1) per component below instead of rescanned. When
        // AllComparisons is empty (cohesion off — see mergePolicyCanReject above), both are
        // trivially empty and every component reads (0, 0), which is byte-identical to what the
        // old per-component scan would have found scanning nothing.
        var recordToComponentIndex = new Dictionary<Guid, int>();
        for (var i = 0; i < components.Count; i++)
            foreach (var id in components[i])
                recordToComponentIndex[id] = i;

        var componentTally = new Dictionary<int, (long Comparisons, long Agreements)>();
        foreach (var comparison in allComparisons)
        {
            if (!recordToComponentIndex.TryGetValue(comparison.LeftId, out var leftIndex) ||
                !recordToComponentIndex.TryGetValue(comparison.RightId, out var rightIndex) ||
                leftIndex != rightIndex)
                continue;

            var (comparisons, agreements) = componentTally.GetValueOrDefault(leftIndex);
            componentTally[leftIndex] = (
                comparisons + 1,
                agreements + (comparison.Band == MatchDecision.AutoMatch ? 1 : 0));
        }

        // Pre-seed clusterByRecord from existing cluster memberships for edge accounting.
        var clusterByRecord = new Dictionary<Guid, Guid>();
        foreach (var cluster in touchedClusters)
            foreach (var id in cluster.MemberEntityRecordIds)
                clusterByRecord[id] = cluster.Id;

        // Materialize components (builds clusterByRecord needed for edge accounting). Each
        // component is consulted with the merge policy BEFORE anything is created or replaced —
        // see MaterializeComponent — so a component whose own comparisons contradict it never
        // reaches this point as a multi-member cluster: it dissolves into singletons instead, and
        // clusterByRecord / affectedClusterIds are populated per-member rather than once per
        // component, because a dissolved component no longer has one cluster id to share.
        var affectedClusterIds = new HashSet<Guid>();
        var singletonClusters = 0;
        for (var i = 0; i < components.Count; i++)
        {
            singletonClusters += MaterializeComponent(
                ws, request, profile, components[i], touchedClusters, edges,
                componentTally.GetValueOrDefault(i), incomingIds, now,
                clusterByRecord, affectedClusterIds);
        }

        // Attribute every comparison the engine made this run — including sub-review ones the
        // edges list never carries — to the cluster holding both its endpoints, now that
        // clusterByRecord reflects FINAL (post-merge) membership. A comparison whose endpoints
        // land in different clusters says nothing about either and is discarded here: it is
        // never turned into a row, only ever into +1 on two integers belonging to one cluster.
        var cohesionDeltas = new Dictionary<Guid, (long Comparisons, long Agreements)>();
        foreach (var comparison in allComparisons)
        {
            if (!clusterByRecord.TryGetValue(comparison.LeftId, out var leftCluster) ||
                !clusterByRecord.TryGetValue(comparison.RightId, out var rightCluster) ||
                leftCluster != rightCluster)
                continue;

            var (comparisons, agreements) = cohesionDeltas.GetValueOrDefault(leftCluster);
            cohesionDeltas[leftCluster] = (
                comparisons + 1,
                agreements + (comparison.Band == MatchDecision.AutoMatch ? 1 : 0));
        }
        if (cohesionDeltas.Count > 0)
        {
            // [M2] ws.Clusters.First + RemoveAll per cluster (the original shape here) is
            // O(clusters) EACH, done once per touched cluster — O(clusters^2) overall. Indexing
            // once up front and writing back by position makes the whole loop O(clusters +
            // cohesionDeltas.Count). Safe because nothing between building the index and using it
            // adds or removes from ws.Clusters — this loop only replaces entries it already knows
            // the position of.
            var clusterIndexById = new Dictionary<Guid, int>();
            for (var i = 0; i < ws.Clusters.Count; i++)
                clusterIndexById[ws.Clusters[i].Id] = i;

            foreach (var (clusterId, delta) in cohesionDeltas)
            {
                // ReplaceCluster/MergeClusters (above, inside MaterializeComponent) already carried
                // each cluster's PRIOR stored counts forward onto the object now in ws.Clusters —
                // this only adds THIS run's tally on top, so a cluster touched across many ingests
                // keeps accumulating rather than resetting.
                var index = clusterIndexById[clusterId];
                var cluster = ws.Clusters[index];
                ws.Clusters[index] = WithCohesionCounts(
                    cluster,
                    cluster.ComparisonsInside + delta.Comparisons,
                    cluster.AgreementsInside + delta.Agreements);
            }
        }

        // Add MatchEdges for auto-band edges whose endpoints resolve into the same cluster (lc == rc).
        // Auto-band bridge edges end with both endpoints in the survivor (lc == rc after component merge)
        // and are also recorded here. Only review-band cross-cluster edges become cluster_merge_suggestion
        // review tasks (see CreateBatchReviewTasks).
        var autoMatches = 0;
        var autoMergedIncomingIds = new HashSet<Guid>();
        foreach (var edge in edges.Where(e => e.Band == MatchDecision.AutoMatch
                                              && (incomingIds.Contains(e.LeftId) || incomingIds.Contains(e.RightId))))
        {
            if (!clusterByRecord.TryGetValue(edge.LeftId, out var lc) ||
                !clusterByRecord.TryGetValue(edge.RightId, out var rc) ||
                lc != rc)
                continue; // bridge case handled by CreateBatchReviewTasks

            ws.MatchEdges.Add(new MatchEdge
            {
                Id = Guid.NewGuid(),
                ProjectId = request.ProjectId,
                IngestBatchId = request.IngestBatchId,
                LeftEntityRecordId = edge.LeftId,
                RightEntityRecordId = edge.RightId,
                Score = edge.Score,
                Method = "incremental",
                Decision = "auto",
                Breakdown = edge.Breakdown,
                Scorer = profile.ScoringStrategy,
                ProfileContentType = profile.ContentType,
                ProfileFingerprint = ProfileFingerprint.Of(profile),
                CreatedAt = now
            });
            autoMatches++;
            if (incomingIds.Contains(edge.LeftId)) autoMergedIncomingIds.Add(edge.LeftId);
            if (incomingIds.Contains(edge.RightId)) autoMergedIncomingIds.Add(edge.RightId);
        }

        var existingClusterIds = touchedClusters.Select(c => c.Id).ToHashSet();
        var reviewTasks = CreateBatchReviewTasks(ws, request, edges, clusterByRecord, incomingIds, autoMergedIncomingIds, existingClusterIds, now);

        var versionsCreated = UpdateGoldenRecords(ws, project, profile, request.IngestBatchId, affectedClusterIds, now);

        // Derive the targeted mutation set from the mutated working set.
        var endGoldenClusterIds = ws.GoldenRecords.Select(g => g.ClusterId).ToHashSet();
        var mutations = new MutationSet();
        mutations.RecordsToInsert.AddRange(incomingRecords);
        mutations.ClustersToUpsert.AddRange(ws.Clusters);
        mutations.GoldenRecordsToUpsert.AddRange(ws.GoldenRecords);
        mutations.GoldenRecordClusterIdsToClear.AddRange(seededGoldenClusterIds.Where(id => !endGoldenClusterIds.Contains(id)));
        mutations.VersionsToInsert.AddRange(ws.GoldenRecordVersions.Where(v => !seededVersionIds.Contains(v.Id)));
        mutations.EdgesToInsert.AddRange(ws.MatchEdges);
        mutations.ReviewTasksToInsert.AddRange(ws.ReviewTasks);
        mutations.MergeEventsToInsert.AddRange(ws.ClusterMergeEvents);
        mutations.DissolutionEventsToInsert.AddRange(ws.ClusterDissolutionEvents);

        var result = new IncrementalIngestResult(incomingRecords.Count, autoMatches, reviewTasks, singletonClusters, versionsCreated);
        return (result, mutations);
    }

    /// <summary>
    /// Applies the per-call overrides the request carries — retrieval strategy and the two
    /// thresholds — leaving every other profile setting exactly as configured. Written as a
    /// <c>with</c> expression on purpose: the hand-written copy this replaced dropped
    /// MaxBlockSize, silently disabling block suppression for the whole durable path.
    /// </summary>
    private static MatchingProfile WithCallOverrides(MatchingProfile profile, string retrievalStrategy, double autoMatchThreshold, double reviewThreshold)
        => profile with
        {
            CandidateRetrievalStrategy = retrievalStrategy,
            AutoMatchThreshold = autoMatchThreshold,
            ReviewThreshold = reviewThreshold
        };

    // comparisonsInside/agreementsInside are the counts to CARRY FORWARD onto the rebuilt cluster
    // (the caller's job to compute — single existing cluster keeps its own; a merge sums all
    // absorbed clusters'). Rebuilding without them is the trap: a cluster reconstructed with the
    // defaults silently resets to 0/0, which reads as perfectly cohesive rather than "unknown."
    private static void ReplaceCluster(
        ResolutionWorkingSet ws, Cluster cluster, IReadOnlyList<Guid> members, long comparisonsInside, long agreementsInside)
    {
        ws.Clusters.RemoveAll(c => c.Id == cluster.Id);
        ws.Clusters.Add(new Cluster
        {
            Id = cluster.Id,
            ProjectId = cluster.ProjectId,
            MemberEntityRecordIds = members.Distinct().ToList(),
            CreatedAt = cluster.CreatedAt,
            Status = cluster.Status,
            MergedIntoClusterId = cluster.MergedIntoClusterId,
            ComparisonsInside = comparisonsInside,
            AgreementsInside = agreementsInside
        });
    }

    private static Cluster WithCohesionCounts(Cluster cluster, long comparisonsInside, long agreementsInside) => new()
    {
        Id = cluster.Id,
        ProjectId = cluster.ProjectId,
        MemberEntityRecordIds = cluster.MemberEntityRecordIds,
        CreatedAt = cluster.CreatedAt,
        Status = cluster.Status,
        MergedIntoClusterId = cluster.MergedIntoClusterId,
        ComparisonsInside = comparisonsInside,
        AgreementsInside = agreementsInside
    };

    private static int UpdateGoldenRecords(
        ResolutionWorkingSet ws,
        Project project,
        MatchingProfile profile,
        Guid ingestBatchId,
        IEnumerable<Guid> affectedClusterIds,
        DateTimeOffset now)
    {
        var mergeIndex = project.MergeConfiguration?.MergeFields
            .ToDictionary(field => field.FieldName, field => field.SourcePriority, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        // Falls back to the conventional "source" column when the profile doesn't declare one
        // (e.g. the built-in person profile) — durable ingestion has always assumed that
        // convention, and a project can attach a merge policy's source priority without the
        // profile itself declaring a SourceIdentifier field.
        var sourceField = profile.Fields
            .FirstOrDefault(f => f.SemanticType == SemanticFieldType.SourceIdentifier)?.Name ?? "source";

        var versionsCreated = 0;
        foreach (var clusterId in affectedClusterIds.Distinct())
        {
            var cluster = ws.Clusters.First(c => c.Id == clusterId);
            var memberIdSet = cluster.MemberEntityRecordIds.ToHashSet();
            var members = ws.EntityRecords
                .Where(r => r.ProjectId == project.Id && memberIdSet.Contains(r.Id))
                .Select(r => r.Fields)
                .ToList();
            var fields = GoldenRecordMerge.MergeFields(members, mergeIndex, sourceField);
            var golden = ws.GoldenRecords.FirstOrDefault(g => g.ProjectId == project.Id && g.ClusterId == clusterId);
            if (golden is not null && GoldenRecordMerge.DictionaryEquals(golden.Fields, fields))
                continue;

            var versionId = Guid.NewGuid();
            if (golden is null)
            {
                golden = new GoldenRecord
                {
                    Id = Guid.NewGuid(),
                    ProjectId = project.Id,
                    ClusterId = clusterId,
                    CurrentVersionId = versionId,
                    Fields = fields,
                    UpdatedAt = now
                };
            }
            else
            {
                ws.GoldenRecords.RemoveAll(g => g.Id == golden.Id);
                golden = new GoldenRecord
                {
                    Id = golden.Id,
                    ProjectId = golden.ProjectId,
                    ClusterId = golden.ClusterId,
                    CurrentVersionId = versionId,
                    Fields = fields,
                    UpdatedAt = now
                };
            }

            ws.GoldenRecords.Add(golden);
            ws.GoldenRecordVersions.Add(new GoldenRecordVersion
            {
                Id = versionId,
                GoldenRecordId = golden.Id,
                ProjectId = project.Id,
                ClusterId = clusterId,
                IngestBatchId = ingestBatchId,
                VersionNumber = ws.GoldenRecordVersions.Count(v => v.GoldenRecordId == golden.Id) + 1,
                Fields = fields,
                CreatedAt = now
            });
            versionsCreated++;
        }

        return versionsCreated;
    }

    private sealed record ResolutionEdge(Guid LeftId, Guid RightId, double Score, MatchDecision Band, IReadOnlyList<MatchScoreFactor> Breakdown);

    // AllComparisons is a SEPARATE dedup'd set from Edges: Edges keeps the review-threshold
    // filter that bounds what becomes a MatchEdge / review task / auto-merge (the frozen
    // baseline depends on that), while AllComparisons carries every comparison the engine made
    // this run — including the below-review ones Edges was always built to exclude — so cohesion
    // accounting can see the population the audit measures rather than the population the
    // decision path acts on.
    private (IReadOnlyList<ResolutionEdge> Edges, IReadOnlyList<ResolutionEdge> AllComparisons) BuildResolutionEdges(
        IReadOnlyList<EntityRecord> incoming,
        IReadOnlyList<EntityRecord> existing,
        MatchingProfile existingCallProfile,
        MatchingProfile batchCallProfile,
        IncrementalIngestRequest request,
        bool captureAllComparisons)
    {
        var edges = new Dictionary<(Guid, Guid), ResolutionEdge>();
        // [C1] Null, not an empty dictionary, when the merge policy cannot reject anything under
        // this profile — the same reasoning as CorpusAuditService's own `comparisons` local:
        // retaining every sub-review pair (each carrying a Breakdown list) here only for a rollup
        // that MaterializeComponent used to rescan per component was the quadratic blowup; when
        // cohesion cannot act, there is nothing for AllComparisons to be read for.
        var allComparisons = captureAllComparisons ? new Dictionary<(Guid, Guid), ResolutionEdge>() : null;

        // Built once per resolve rather than per edge: constructing it validates the request's
        // thresholds, and an invalid pair should fail the whole call rather than the first edge
        // that happens to be scored. Scale comes from the profile's OWN resolved scorer (both
        // call profiles share the same ScoringStrategy — WithCallOverrides only ever touches
        // retrieval + the two threshold values), not an assumed ScoreScale.UnitInterval: an
        // evidence-scored profile's thresholds are absolute bits of log-odds evidence, and
        // validating them against [0,1] rejects every one that is actually valid.
        var thresholds = new MatchThresholds(
            request.AutoMatchThreshold, request.ReviewThreshold, _engine.ScaleOf(existingCallProfile));

        void AddComparison(Guid a, Guid b, double score, IReadOnlyList<MatchScoreFactor> breakdown)
        {
            if (a == b) return;

            // comparable: true — a comparison only reaches here because the engine produced a
            // scored candidate, so there was something to compare. A pair with nothing in common
            // never reaches here; it is discarded before scoring.
            var band = MatchBandClassifier.Classify(score, comparable: true, thresholds);
            var (lo, hi) = a.CompareTo(b) <= 0 ? (a, b) : (b, a);

            // Same pair can be scored from both directions (each side's own batch-mate pass);
            // keep-max on the canonical (lo, hi) key is the existing edges policy, applied here
            // identically so AllComparisons and Edges never disagree about the score of a pair
            // both contain.
            if (allComparisons is not null &&
                (!allComparisons.TryGetValue((lo, hi), out var currentAll) || score > currentAll.Score))
                allComparisons[(lo, hi)] = new ResolutionEdge(lo, hi, score, band, breakdown);

            if (band == MatchDecision.NoMatch) return;
            if (!edges.TryGetValue((lo, hi), out var current) || score > current.Score)
                edges[(lo, hi)] = new ResolutionEdge(lo, hi, score, band, breakdown);
        }

        // Edge production is read-only and independent per incoming record (Lucene retrieval +
        // pure scoring; no IResolutionContext access). Run it in parallel, collecting each
        // record's raw candidate edges by index, then reduce SEQUENTIALLY in index order so
        // AddComparison's keep-max / first-wins-on-tie semantics are byte-identical to the
        // sequential implementation regardless of _degreeOfParallelism (see the DOP determinism
        // test).
        var perRecord = new List<(Guid From, Guid To, double Score, IReadOnlyList<MatchScoreFactor> Breakdown)>[incoming.Count];
        var options = new ParallelOptions { MaxDegreeOfParallelism = _degreeOfParallelism };
        Parallel.For(0, incoming.Count, options, i =>
        {
            var record = incoming[i];
            var local = new List<(Guid, Guid, double, IReadOnlyList<MatchScoreFactor>)>();

            var corpus = _hasIndex ? Array.Empty<EntityRecord>() : (IReadOnlyCollection<EntityRecord>)existing;
            // The out-param overload reports every scored candidate, not only the ones clearing
            // ReviewThreshold — the population cohesion counting needs. This does not change what
            // becomes a MatchEdge: that still only ever comes from the Edges half below.
            _ = _engine.Resolve(record, corpus, existingCallProfile, out var existingAll);
            foreach (var c in existingAll.Where(c => c.Record.ProjectId == request.ProjectId))
                local.Add((record.Id, c.Record.Id, c.Score, ToFactors(c.Breakdown)));

            var batchMates = incoming.Where(r => r.Id != record.Id).ToList();
            if (batchMates.Count > 0)
            {
                _ = _engine.Resolve(record, batchMates, batchCallProfile, out var batchAll);
                foreach (var c in batchAll)
                    local.Add((record.Id, c.Record.Id, c.Score, ToFactors(c.Breakdown)));
            }

            perRecord[i] = local;
        });

        foreach (var local in perRecord)
            foreach (var (from, to, score, breakdown) in local)
                AddComparison(from, to, score, breakdown);

        return (edges.Values.ToList(), allComparisons?.Values.ToList() ?? []);
    }

    private static IReadOnlyList<IReadOnlyList<Guid>> ResolveComponents(
        IReadOnlyList<EntityRecord> incoming,
        IReadOnlyList<Cluster> touchedClusters,
        IReadOnlyList<ResolutionEdge> edges)
    {
        var strategy = new UnionFindClusteringStrategy();

        var nodeIds = new HashSet<Guid>(incoming.Select(r => r.Id));
        foreach (var cluster in touchedClusters)
            foreach (var member in cluster.MemberEntityRecordIds)
                nodeIds.Add(member);

        var pairs = new List<(string Left, string Right)>();
        // Seed each touched cluster as one pre-merged component.
        foreach (var cluster in touchedClusters)
            for (var i = 1; i < cluster.MemberEntityRecordIds.Count; i++)
                pairs.Add((cluster.MemberEntityRecordIds[0].ToString(), cluster.MemberEntityRecordIds[i].ToString()));
        // Union only along auto-band edges (Option A).
        foreach (var edge in edges.Where(e => e.Band == MatchDecision.AutoMatch))
            pairs.Add((edge.LeftId.ToString(), edge.RightId.ToString()));

        return strategy
            .Cluster(nodeIds.Select(id => id.ToString()), pairs)
            .Select(component => (IReadOnlyList<Guid>)component.Select(Guid.Parse).ToList())
            .ToList();
    }

    // Returns the number of 1-member (singleton) clusters this component contributed this run —
    // either the classic "brand-new component of size 1" case, or every member of a dissolved
    // component (see DissolveComponent).
    private int MaterializeComponent(
        ResolutionWorkingSet ws,
        IncrementalIngestRequest request,
        MatchingProfile profile,
        IReadOnlyList<Guid> component,
        List<Cluster> touchedClusters,
        IReadOnlyList<ResolutionEdge> edges,
        (long Comparisons, long Agreements) thisRunTally,
        IReadOnlySet<Guid> incomingIds,
        DateTimeOffset now,
        Dictionary<Guid, Guid> clusterByRecord,
        HashSet<Guid> affectedClusterIds)
    {
        var componentSet = component.ToHashSet();
        var existingClusters = touchedClusters
            .Where(c => c.MemberEntityRecordIds.Any(componentSet.Contains))
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .ToList();

        // Consulted BEFORE anything is created or replaced. The counts here are exactly what the
        // cluster WOULD carry if materialized: prior stored counts carried forward the same way
        // ReplaceCluster/MergeClusters carry them below, plus this run's own within-component
        // comparisons — [C1] pre-tallied once for every component in a single O(allComparisons)
        // pass in Resolve (recordToComponentIndex / componentTally), rather than rescanned here per
        // component, which was O(components x allComparisons) on a large single-batch ingest.
        // Members comes from the component's own record count — never from a Cluster object, which
        // for a brand-new component does not exist yet and for default(ClusterEvidenceCounts) would
        // silently read as a fully-agreeing zero-member cluster.
        var (thisRunComparisons, thisRunAgreements) = thisRunTally;
        var (priorComparisons, priorAgreements) = existingClusters.Count switch
        {
            0 => (0L, 0L),
            1 => (existingClusters[0].ComparisonsInside, existingClusters[0].AgreementsInside),
            _ => (existingClusters.Sum(c => c.ComparisonsInside), existingClusters.Sum(c => c.AgreementsInside))
        };
        var counts = new ClusterEvidenceCounts(
            component.Count, priorComparisons + thisRunComparisons, priorAgreements + thisRunAgreements);

        var verdict = _mergePolicy.Evaluate(counts, profile);
        if (verdict != ClusterMergeVerdict.Accepted)
        {
            return DissolveComponent(
                ws, request, verdict, component, existingClusters, counts, incomingIds, now,
                clusterByRecord, affectedClusterIds);
        }

        if (existingClusters.Count == 0)
        {
            var cluster = new Cluster
            {
                Id = Guid.NewGuid(),
                ProjectId = request.ProjectId,
                MemberEntityRecordIds = component.Distinct().ToList(),
                CreatedAt = now
            };
            ws.Clusters.Add(cluster);
            AssignComponentToCluster(cluster.Id, component, incomingIds, clusterByRecord, affectedClusterIds);
            return component.Count == 1 ? 1 : 0;
        }

        if (existingClusters.Count == 1)
        {
            var target = existingClusters[0];
            var members = target.MemberEntityRecordIds.Concat(component).Distinct().ToList();
            ReplaceCluster(ws, target, members, target.ComparisonsInside, target.AgreementsInside);
            AssignComponentToCluster(target.Id, component, incomingIds, clusterByRecord, affectedClusterIds);
            return 0;
        }

        var survivorId = MergeClusters(ws, request, existingClusters, component, edges, now);
        AssignComponentToCluster(survivorId, component, incomingIds, clusterByRecord, affectedClusterIds);
        return 0;
    }

    private static void AssignComponentToCluster(
        Guid clusterId,
        IReadOnlyList<Guid> component,
        IReadOnlySet<Guid> incomingIds,
        Dictionary<Guid, Guid> clusterByRecord,
        HashSet<Guid> affectedClusterIds)
    {
        foreach (var recordId in component)
            clusterByRecord[recordId] = clusterId;
        // Only mark a cluster as affected when it received at least one new (incoming) record.
        if (component.Any(incomingIds.Contains))
            affectedClusterIds.Add(clusterId);
    }

    // The component does NOT form: no subset selection, no peel-back — every obvious peel-back
    // algorithm is order-dependent, which is what disqualified the mechanism this replaces. Every
    // member reverts to its own singleton (this codebase's existing representation of
    // "unclustered" — see the brand-new, existingClusters.Count == 0 branch above), every
    // pre-existing cluster the component absorbed is tombstoned, and a ClusterDissolutionEvent
    // records the numbers that refused it — dissolution must never be silent.
    private static int DissolveComponent(
        ResolutionWorkingSet ws,
        IncrementalIngestRequest request,
        ClusterMergeVerdict verdict,
        IReadOnlyList<Guid> component,
        IReadOnlyList<Cluster> existingClusters,
        ClusterEvidenceCounts counts,
        IReadOnlySet<Guid> incomingIds,
        DateTimeOffset now,
        Dictionary<Guid, Guid> clusterByRecord,
        HashSet<Guid> affectedClusterIds)
    {
        // Tombstones BEFORE singletons, deliberately. Both loops touch ws.Clusters (which becomes
        // mutations.ClustersToUpsert in the order it is built — see Resolve), and a tombstone
        // PRESERVES the dissolved cluster's own MemberEntityRecordIds rather than clearing them
        // (see the comment below), so every dissolved record's id appears in TWO rows: its fresh
        // singleton and the tombstone of the cluster it used to belong to. A store that applies
        // ClustersToUpsert last-write-wins (mapping a record to whichever row mentioning it comes
        // LAST) would resolve that record onto the dead tombstone if singletons were written
        // first — the opposite of what dissolution means. Writing tombstones first makes the
        // singleton the last, and therefore winning, row for every record it names.
        foreach (var existing in existingClusters)
        {
            // Tombstoned the same way MergeClusters tombstones a loser: Status == "merged" is this
            // schema's only "not active" marker (every ListClusters/GetActiveClusters query on both
            // backends already filters on it), reused here rather than inventing a second status
            // string every one of those filters would need to learn about. MergedIntoClusterId
            // staying null IS the signal that distinguishes a dissolution tombstone from an
            // absorption tombstone at this row; the ClusterDissolutionEvent below carries the why.
            // MemberEntityRecordIds/ComparisonsInside/AgreementsInside are preserved, not reset, for
            // the same reason a merge loser's are: a post-mortem audit needs the cluster's own
            // pre-dissolution history, not a fresh 0/0 that reads as "never compared."
            ws.Clusters.RemoveAll(c => c.Id == existing.Id);
            ws.Clusters.Add(new Cluster
            {
                Id = existing.Id,
                ProjectId = existing.ProjectId,
                MemberEntityRecordIds = existing.MemberEntityRecordIds,
                CreatedAt = existing.CreatedAt,
                Status = "merged",
                MergedIntoClusterId = null,
                ComparisonsInside = existing.ComparisonsInside,
                AgreementsInside = existing.AgreementsInside
            });
            // Its golden record is stale evidence for a cluster that no longer exists. Removing it
            // from the working set here lets Resolve's end-of-call seeded/end diff carry it into
            // GoldenRecordClusterIdsToClear automatically — the same mechanism a merge loser's
            // golden already relies on, not a second one.
            ws.GoldenRecords.RemoveAll(g => g.ClusterId == existing.Id);
        }

        foreach (var recordId in component)
        {
            var singleton = new Cluster
            {
                Id = Guid.NewGuid(),
                ProjectId = request.ProjectId,
                MemberEntityRecordIds = [recordId],
                CreatedAt = now
            };
            ws.Clusters.Add(singleton);
            clusterByRecord[recordId] = singleton.Id;
            if (incomingIds.Contains(recordId))
                affectedClusterIds.Add(singleton.Id);
        }

        // One event per previously-published cluster this component absorbed — each individually
        // queryable ("why did cluster X disappear") even when a single bridging record dissolved
        // more than one at once — sharing the same evidence, because they failed together as one
        // component. Exactly one event with PreviousClusterId == null when the component never had
        // a previously-published cluster at all (it was never formed, not re-checked).
        IEnumerable<Guid?> previousClusterIds = existingClusters.Count > 0
            ? existingClusters.Select(c => (Guid?)c.Id)
            : new Guid?[] { null };
        foreach (var previousClusterId in previousClusterIds)
        {
            ws.ClusterDissolutionEvents.Add(new ClusterDissolutionEvent
            {
                Id = Guid.NewGuid(),
                ProjectId = request.ProjectId,
                MemberEntityRecordIds = component.Distinct().ToList(),
                PreviousClusterId = previousClusterId,
                Reason = verdict.ToString(),
                ComparisonsInside = counts.ComparisonsInside,
                AgreementsInside = counts.AgreementsInside,
                IngestBatchId = request.IngestBatchId,
                CreatedAt = now
            });
        }

        return component.Count;
    }

    private static Guid MergeClusters(
        ResolutionWorkingSet ws,
        IncrementalIngestRequest request,
        IReadOnlyList<Cluster> existingClusters,
        IReadOnlyList<Guid> component,
        IReadOnlyList<ResolutionEdge> edges,
        DateTimeOffset now)
    {
        // Deterministic survivor: oldest CreatedAt, tie-break smallest Id.
        var survivor = existingClusters.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).First();
        var losers = existingClusters.Where(c => c.Id != survivor.Id).ToList();

        var existingMemberIds = existingClusters.SelectMany(c => c.MemberEntityRecordIds).ToHashSet();
        var triggerIds = component.Where(id => !existingMemberIds.Contains(id)).ToList();
        var componentSet = component.ToHashSet();
        var topEdge = edges
            .Where(e => e.Band == MatchDecision.AutoMatch && componentSet.Contains(e.LeftId) && componentSet.Contains(e.RightId))
            .OrderByDescending(e => e.Score)
            .FirstOrDefault();

        foreach (var loser in losers)
        {
            ws.ClusterMergeEvents.Add(new ClusterMergeEvent
            {
                Id = Guid.NewGuid(),
                ProjectId = request.ProjectId,
                SurvivorClusterId = survivor.Id,
                AbsorbedClusterId = loser.Id,
                AbsorbedMemberEntityRecordIds = loser.MemberEntityRecordIds.ToList(),
                TriggerRecordIds = triggerIds,
                Score = topEdge?.Score ?? 0,
                Breakdown = topEdge?.Breakdown ?? [],
                IngestBatchId = request.IngestBatchId,
                CreatedAt = now
            });

            // Tombstone the loser: retain its GoldenRecordVersions and MemberEntityRecordIds (together with the
            // event's AbsorbedMemberEntityRecordIds) so the pre-merge state can be reconstructed (unmerge; spec D2).
            // The loser's current GoldenRecord row is removed; its version history is preserved.
            ws.Clusters.RemoveAll(c => c.Id == loser.Id);
            ws.Clusters.Add(new Cluster
            {
                Id = loser.Id,
                ProjectId = loser.ProjectId,
                MemberEntityRecordIds = loser.MemberEntityRecordIds,
                CreatedAt = loser.CreatedAt,
                Status = "merged",
                MergedIntoClusterId = survivor.Id,
                // Preserved, not reset, for the same reason MemberEntityRecordIds is preserved on
                // a tombstone: an unmerge needs to restore the loser's own pre-merge cohesion
                // history, not hand it a fresh 0/0 that reads as "never compared."
                ComparisonsInside = loser.ComparisonsInside,
                AgreementsInside = loser.AgreementsInside
            });
            ws.GoldenRecords.RemoveAll(g => g.ClusterId == loser.Id);
        }

        var mergedMembers = existingClusters
            .SelectMany(c => c.MemberEntityRecordIds)
            .Concat(component)
            .Distinct()
            .ToList();
        // The survivor's carried-forward counts are every absorbed cluster's own history summed —
        // not just the survivor's — because a merge is exactly the claim that these were always
        // one entity; its cohesion evidence should read as one cluster's from here on. This run's
        // own within-component comparisons are added afterward, uniformly for every materialized
        // cluster (see the cohesion tally after the component loop in Resolve).
        ReplaceCluster(
            ws, survivor, mergedMembers,
            existingClusters.Sum(c => c.ComparisonsInside),
            existingClusters.Sum(c => c.AgreementsInside));
        return survivor.Id;
    }

    private static int CreateBatchReviewTasks(
        ResolutionWorkingSet ws,
        IncrementalIngestRequest request,
        IReadOnlyList<ResolutionEdge> edges,
        IReadOnlyDictionary<Guid, Guid> clusterByRecord,
        IReadOnlySet<Guid> incomingIds,
        IReadOnlySet<Guid> autoMergedIncomingIds,
        IReadOnlySet<Guid> existingClusterIds,
        DateTimeOffset now)
    {
        var created = 0;
        foreach (var edge in edges.Where(e => e.Band == MatchDecision.Review))
        {
            // Determinism fix: NewEntityRecordId = the incoming endpoint (not the canonical lo GUID).
            // If both endpoints are incoming (incoming<->incoming review), fall back to the lo GUID
            // (edge.LeftId is already normalized lo) for a stable, order-independent choice.
            var leftIsIncoming = incomingIds.Contains(edge.LeftId);
            var (newId, candidateId) = leftIsIncoming
                ? (edge.LeftId, edge.RightId)
                : (edge.RightId, edge.LeftId);

            clusterByRecord.TryGetValue(newId, out var newCluster);
            clusterByRecord.TryGetValue(candidateId, out var candidateCluster);
            // "cluster_merge_suggestion" when both sides are in pre-existing, distinct clusters
            // (weak-bridge: X auto-joined C1 but only review-matched C2).
            var bridges = existingClusterIds.Contains(newCluster) && existingClusterIds.Contains(candidateCluster)
                          && newCluster != candidateCluster;

            // Skip non-bridge reviews where an incoming endpoint already auto-merged into a cluster
            // (auto-match wins over review-band for same-cluster pairs).
            // Bridge reviews must always emit — that is the cluster_merge_suggestion path.
            if (!bridges && (autoMergedIncomingIds.Contains(edge.LeftId) || autoMergedIncomingIds.Contains(edge.RightId)))
                continue;

            ws.ReviewTasks.Add(new ReviewTask
            {
                Id = Guid.NewGuid(),
                ProjectId = request.ProjectId,
                IngestBatchId = request.IngestBatchId,
                NewEntityRecordId = newId,
                CandidateEntityRecordId = candidateId,
                Score = edge.Score,
                Reason = bridges ? "cluster_merge_suggestion" : "review_threshold",
                Breakdown = edge.Breakdown,
                LeftClusterId = bridges ? newCluster : null,
                RightClusterId = bridges ? candidateCluster : null,
                Status = "open",
                CreatedAt = now
            });
            created++;
        }
        // Auto-band bridge edges are handled by MergeClusters (Task 3); no review tasks emitted here.
        return created;
    }

    private static IReadOnlyList<MatchScoreFactor> ToFactors(IReadOnlyList<ScoreContribution> breakdown)
        => breakdown
            .Select(c => new MatchScoreFactor(c.Signal, c.Value, c.Weight, c.Contribution))
            .ToList();
}
