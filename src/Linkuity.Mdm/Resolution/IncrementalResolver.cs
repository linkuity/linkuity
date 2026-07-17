using Linkuity.Core.Models;
using Linkuity.Matching;
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
    private readonly int _degreeOfParallelism;

    public IncrementalResolver(IMatchingEngine engine, bool hasIndex, int degreeOfParallelism = 1)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _hasIndex = hasIndex;
        _degreeOfParallelism = Math.Max(1, degreeOfParallelism);
    }

    public IReadOnlyList<string> GenerateBlockingKeys(EntityRecord record, MatchingProfile profile)
        => _engine.GenerateBlockingKeys(record, profile);

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

        var edges = BuildResolutionEdges(incomingRecords, existingRecords, callProfile, batchCallProfile, request);

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

        // Pre-seed clusterByRecord from existing cluster memberships for edge accounting.
        var clusterByRecord = new Dictionary<Guid, Guid>();
        foreach (var cluster in touchedClusters)
            foreach (var id in cluster.MemberEntityRecordIds)
                clusterByRecord[id] = cluster.Id;

        // Materialize components (builds clusterByRecord needed for edge accounting).
        var affectedClusterIds = new HashSet<Guid>();
        var singletonClusters = 0;
        foreach (var component in components)
        {
            var clusterId = MaterializeComponent(ws, request, component, touchedClusters, edges, now, out var isSingleton);
            // Only mark a cluster as affected when it received at least one new (incoming) record.
            if (component.Any(incomingIds.Contains))
                affectedClusterIds.Add(clusterId);
            if (isSingleton) singletonClusters++;
            foreach (var recordId in component)
                clusterByRecord[recordId] = clusterId;
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
                CreatedAt = now
            });
            autoMatches++;
            if (incomingIds.Contains(edge.LeftId)) autoMergedIncomingIds.Add(edge.LeftId);
            if (incomingIds.Contains(edge.RightId)) autoMergedIncomingIds.Add(edge.RightId);
        }

        var existingClusterIds = touchedClusters.Select(c => c.Id).ToHashSet();
        var reviewTasks = CreateBatchReviewTasks(ws, request, edges, clusterByRecord, incomingIds, autoMergedIncomingIds, existingClusterIds, now);

        var versionsCreated = UpdateGoldenRecords(ws, project, request.IngestBatchId, affectedClusterIds, now);

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

        var result = new IncrementalIngestResult(incomingRecords.Count, autoMatches, reviewTasks, singletonClusters, versionsCreated);
        return (result, mutations);
    }

    private static MatchingProfile WithCallOverrides(MatchingProfile profile, string retrievalStrategy, double autoMatchThreshold, double reviewThreshold)
        => new()
        {
            ContentType = profile.ContentType,
            Fields = profile.Fields,
            NormalizationStrategy = profile.NormalizationStrategy,
            BlockingStrategies = profile.BlockingStrategies,
            CandidateRetrievalStrategy = retrievalStrategy,
            SimilarityStrategy = profile.SimilarityStrategy,
            ScoringStrategy = profile.ScoringStrategy,
            DecisionStrategy = profile.DecisionStrategy,
            ClusteringStrategy = profile.ClusteringStrategy,
            AutoMatchThreshold = autoMatchThreshold,
            ReviewThreshold = reviewThreshold,
            ReviewFloorGate = profile.ReviewFloorGate
        };

    private static void ReplaceCluster(ResolutionWorkingSet ws, Cluster cluster, IReadOnlyList<Guid> members)
    {
        ws.Clusters.RemoveAll(c => c.Id == cluster.Id);
        ws.Clusters.Add(new Cluster
        {
            Id = cluster.Id,
            ProjectId = cluster.ProjectId,
            MemberEntityRecordIds = members.Distinct().ToList(),
            CreatedAt = cluster.CreatedAt,
            Status = cluster.Status,
            MergedIntoClusterId = cluster.MergedIntoClusterId
        });
    }

    private static int UpdateGoldenRecords(
        ResolutionWorkingSet ws,
        Project project,
        Guid ingestBatchId,
        IEnumerable<Guid> affectedClusterIds,
        DateTimeOffset now)
    {
        var versionsCreated = 0;
        foreach (var clusterId in affectedClusterIds.Distinct())
        {
            var cluster = ws.Clusters.First(c => c.Id == clusterId);
            var memberIdSet = cluster.MemberEntityRecordIds.ToHashSet();
            var members = ws.EntityRecords
                .Where(r => r.ProjectId == project.Id && memberIdSet.Contains(r.Id))
                .ToList();
            var fields = GoldenRecordMerge.MergeFields(project, members);
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

    private IReadOnlyList<ResolutionEdge> BuildResolutionEdges(
        IReadOnlyList<EntityRecord> incoming,
        IReadOnlyList<EntityRecord> existing,
        MatchingProfile existingCallProfile,
        MatchingProfile batchCallProfile,
        IncrementalIngestRequest request)
    {
        var edges = new Dictionary<(Guid, Guid), ResolutionEdge>();

        void AddEdge(Guid a, Guid b, double score, IReadOnlyList<MatchScoreFactor> breakdown)
        {
            if (a == b) return;
            var band = score >= request.AutoMatchThreshold ? MatchDecision.AutoMatch
                     : score >= request.ReviewThreshold ? MatchDecision.Review
                     : MatchDecision.NoMatch;
            if (band == MatchDecision.NoMatch) return;
            var (lo, hi) = a.CompareTo(b) <= 0 ? (a, b) : (b, a);
            if (!edges.TryGetValue((lo, hi), out var current) || score > current.Score)
                edges[(lo, hi)] = new ResolutionEdge(lo, hi, score, band, breakdown);
        }

        // Edge production is read-only and independent per incoming record (Lucene retrieval +
        // pure scoring; no IResolutionContext access). Run it in parallel, collecting each
        // record's raw candidate edges by index, then reduce SEQUENTIALLY in index order so
        // AddEdge's keep-max / first-wins-on-tie semantics are byte-identical to the sequential
        // implementation regardless of _degreeOfParallelism (see the DOP determinism test).
        var perRecord = new List<(Guid From, Guid To, double Score, IReadOnlyList<MatchScoreFactor> Breakdown)>[incoming.Count];
        var options = new ParallelOptions { MaxDegreeOfParallelism = _degreeOfParallelism };
        Parallel.For(0, incoming.Count, options, i =>
        {
            var record = incoming[i];
            var local = new List<(Guid, Guid, double, IReadOnlyList<MatchScoreFactor>)>();

            var corpus = _hasIndex ? Array.Empty<EntityRecord>() : (IReadOnlyCollection<EntityRecord>)existing;
            var existingMatch = _engine.Resolve(record, corpus, existingCallProfile);
            foreach (var c in existingMatch.Candidates.Where(c => c.Record.ProjectId == request.ProjectId))
                local.Add((record.Id, c.Record.Id, c.Score, ToFactors(c.Breakdown)));

            var batchMates = incoming.Where(r => r.Id != record.Id).ToList();
            if (batchMates.Count > 0)
            {
                var batchMatch = _engine.Resolve(record, batchMates, batchCallProfile);
                foreach (var c in batchMatch.Candidates)
                    local.Add((record.Id, c.Record.Id, c.Score, ToFactors(c.Breakdown)));
            }

            perRecord[i] = local;
        });

        foreach (var local in perRecord)
            foreach (var (from, to, score, breakdown) in local)
                AddEdge(from, to, score, breakdown);

        return edges.Values.ToList();
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

    private Guid MaterializeComponent(
        ResolutionWorkingSet ws,
        IncrementalIngestRequest request,
        IReadOnlyList<Guid> component,
        List<Cluster> touchedClusters,
        IReadOnlyList<ResolutionEdge> edges,
        DateTimeOffset now,
        out bool isSingleton)
    {
        var componentSet = component.ToHashSet();
        var existingClusters = touchedClusters
            .Where(c => c.MemberEntityRecordIds.Any(componentSet.Contains))
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .ToList();

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
            isSingleton = component.Count == 1;
            return cluster.Id;
        }

        if (existingClusters.Count == 1)
        {
            var target = existingClusters[0];
            var members = target.MemberEntityRecordIds.Concat(component).Distinct().ToList();
            ReplaceCluster(ws, target, members);
            isSingleton = false;
            return target.Id;
        }

        var survivorId = MergeClusters(ws, request, existingClusters, component, edges, now);
        isSingleton = false;
        return survivorId;
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
                MergedIntoClusterId = survivor.Id
            });
            ws.GoldenRecords.RemoveAll(g => g.ClusterId == loser.Id);
        }

        var mergedMembers = existingClusters
            .SelectMany(c => c.MemberEntityRecordIds)
            .Concat(component)
            .Distinct()
            .ToList();
        ReplaceCluster(ws, survivor, mergedMembers);
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
