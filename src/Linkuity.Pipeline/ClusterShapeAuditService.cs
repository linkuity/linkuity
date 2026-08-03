using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;

namespace Linkuity.Pipeline;

/// <summary>
/// Measures the internal shape of the clusters the engine produces, split by whether each cluster
/// is correct. See C:\dev\linkuity-notes\cluster-protection-measurement.md.
/// <para>
/// Reports two distributions and draws no conclusion. If correct and over-merged clusters look
/// different, shape-based merge protection is well founded and its thresholds come from these
/// numbers. If they look the same — because the engine compares too few pairs inside a cluster to
/// reveal any shape — then blocking coverage is the binding constraint and no shape rule can work
/// yet. Both are useful answers; only one of them is currently assumed.
/// </para>
/// <para>
/// Deliberately separate from <see cref="CorpusAuditService"/>: this one materializes the
/// auto-merge edge set, which that service is explicitly built never to do. Keeping it apart
/// leaves the baseline gate's memory profile untouched.
/// </para>
/// </summary>
public sealed class ClusterShapeAuditService
{
    private readonly IStrategyRegistry _registry;

    public ClusterShapeAuditService(IStrategyRegistry registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public ClusterShapeResult Audit(
        IReadOnlyList<EntityRecord> records,
        MatchingProfile profile,
        IReadOnlyDictionary<string, string> groundTruth,
        int? maxBlockSize = null,
        int topClusters = 20,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(groundTruth);
        ArgumentOutOfRangeException.ThrowIfLessThan(topClusters, 1);

        CorpusAuditService.ValidateSupportedStrategies(profile);

        var effectiveMax = maxBlockSize ?? profile.MaxBlockSize;
        var normalization = _registry.Normalization[profile.NormalizationStrategy];
        var similarity = _registry.Similarity[profile.SimilarityStrategy];
        var scoring = _registry.Scoring[profile.ScoringStrategy];

        var normalized = records.Select(r => normalization.Normalize(r, profile)).ToArray();
        var index = CorpusAuditService.BuildIndex(records, profile, _registry, ct);

        // Pass 1 — score every candidate pair, union the auto-merges, and KEEP those edges. The
        // edge set is what shape is read from; nothing else in the audit stack retains it.
        var uf = new CorpusAuditService.UnionFind(records.Count);
        var edges = new List<(int Left, int Right)>();
        long candidatePairs = 0;
        CorpusAuditService.ForEachCandidatePair(index, effectiveMax, (l, r) =>
        {
            candidatePairs++;
            var score = CorpusAuditService.ScorePair(normalized, l, r, similarity, scoring, profile, out var comparable, out _);
            if (CorpusAuditService.BandOf(score, comparable, profile) == CorpusBand.Auto)
            {
                uf.Union(l, r);
                edges.Add((l, r));
            }
        }, ct);

        var root = new int[records.Count];
        for (var i = 0; i < records.Count; i++) root[i] = uf.Find(i);

        // Pass 2 — how many pairs inside each cluster were compared AT ALL. No scoring, so this
        // costs a walk and nothing more. Without it a sparse cluster and a disagreeing one are
        // indistinguishable, which is the confounder the whole measurement turns on.
        var comparedInCluster = new Dictionary<int, long>();
        CorpusAuditService.ForEachCandidatePair(index, effectiveMax, (l, r) =>
        {
            if (root[l] == root[r])
                comparedInCluster[root[l]] = comparedInCluster.GetValueOrDefault(root[l]) + 1;
        }, ct);

        var isBridge = EdgeGraph.FindBridges(records.Count, edges);
        var twoEc = EdgeGraph.TwoEdgeConnectedComponents(records.Count, edges, isBridge);

        var rows = BuildRows(records, groundTruth, root, edges, isBridge, twoEc, comparedInCluster);

        var singletons = records.Count - rows.Sum(r => r.Size);
        var counts = new ClusterShapeCounts(
            records.Count,
            records.Count(r => !groundTruth.ContainsKey(r.SourceRecordId)),
            rows.Count + singletons,
            rows.Count,
            singletons,
            edges.Count,
            candidatePairs,
            comparedInCluster.Values.Sum());

        return new ClusterShapeResult(
            counts,
            BuildGroups(rows),
            BuildSizeBands(rows),
            BuildAgreementThresholds(rows),
            rows.OrderByDescending(x => x.Size)
                .ThenBy(x => x.RepresentativeRecordId, StringComparer.Ordinal)
                .Take(topClusters).ToList());
    }

    private static List<ClusterShapeRow> BuildRows(
        IReadOnlyList<EntityRecord> records,
        IReadOnlyDictionary<string, string> groundTruth,
        int[] root,
        IReadOnlyList<(int Left, int Right)> edges,
        bool[] isBridge,
        int[] twoEc,
        IReadOnlyDictionary<int, long> comparedInCluster)
    {
        var members = new Dictionary<int, List<int>>();
        for (var i = 0; i < root.Length; i++)
            (members.TryGetValue(root[i], out var list) ? list : members[root[i]] = []).Add(i);

        var edgeCount = new Dictionary<int, int>();
        var bridgeCount = new Dictionary<int, int>();
        for (var e = 0; e < edges.Count; e++)
        {
            var owner = root[edges[e].Left];
            edgeCount[owner] = edgeCount.GetValueOrDefault(owner) + 1;
            if (isBridge[e]) bridgeCount[owner] = bridgeCount.GetValueOrDefault(owner) + 1;
        }

        var rows = new List<ClusterShapeRow>();
        foreach (var (clusterRoot, ids) in members)
        {
            if (ids.Count < 2) continue;

            var labels = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var i in ids)
                if (groundTruth.TryGetValue(records[i].SourceRecordId, out var label))
                    labels[label] = labels.GetValueOrDefault(label) + 1;

            var twoEcSizes = new Dictionary<int, int>();
            foreach (var i in ids) twoEcSizes[twoEc[i]] = twoEcSizes.GetValueOrDefault(twoEc[i]) + 1;

            rows.Add(new ClusterShapeRow(
                RepresentativeRecordId: ids.Select(i => records[i].SourceRecordId)
                    .Min(StringComparer.Ordinal)!,
                Size: ids.Count,
                LabeledMembers: labels.Values.Sum(),
                DistinctTrueLabels: labels.Count,
                LargestTrueLabelSize: labels.Count == 0 ? 0 : labels.Values.Max(),
                AutoEdges: edgeCount.GetValueOrDefault(clusterRoot),
                ComparedPairs: comparedInCluster.GetValueOrDefault(clusterRoot),
                BridgeCount: bridgeCount.GetValueOrDefault(clusterRoot),
                LargestTwoEdgeConnectedSize: twoEcSizes.Count == 0 ? 0 : twoEcSizes.Values.Max(),
                Verdict: labels.Count switch
                {
                    0 => ClusterVerdict.Unlabeled,
                    1 => ClusterVerdict.Correct,
                    _ => ClusterVerdict.Mixed
                }));
        }
        return rows;
    }

    private static List<ClusterShapeGroupRow> BuildGroups(IReadOnlyList<ClusterShapeRow> rows)
        => Enum.GetValues<ClusterVerdict>().Select(verdict =>
        {
            var group = rows.Where(r => r.Verdict == verdict).ToList();
            var threePlus = group.Where(r => r.Size >= 3).ToList();
            return new ClusterShapeGroupRow(
                verdict,
                group.Count,
                group.Sum(r => (long)r.Size),
                group.Count == 0 ? 0 : group.Max(r => r.Size),
                threePlus.Count,
                threePlus.Count(r => r.SplitByOneEdge),
                threePlus.Count == 0 ? 0 : (double)threePlus.Count(r => r.SplitByOneEdge) / threePlus.Count,
                Median(group.Select(r => r.EdgesPerRecord)),
                Median(group.Select(r => r.ComparedFraction)),
                Median(group.Select(r => r.LargestTwoEdgeConnectedFraction)),
                Median(group.Select(r => r.AgreementRate)));
        }).ToList();

    internal static readonly double[] AgreementThresholds =
        [0.95, 0.90, 0.80, 0.70, 0.60, 0.50, 0.40, 0.30];

    /// <summary>
    /// Sweeps candidate cohesion thresholds and reports both sides of the trade at each. A single
    /// median cannot answer "is this rule worth having" — the answer lives in how fast the two
    /// populations fall away from each other as the line moves.
    /// </summary>
    private static List<ClusterAgreementThresholdRow> BuildAgreementThresholds(
        IReadOnlyList<ClusterShapeRow> rows)
    {
        var correct = rows.Where(r => r.Verdict == ClusterVerdict.Correct).ToList();
        var mixed = rows.Where(r => r.Verdict == ClusterVerdict.Mixed).ToList();

        return AgreementThresholds.Select(t =>
        {
            var c = correct.Where(r => r.AgreementRate < t).ToList();
            var m = mixed.Where(r => r.AgreementRate < t).ToList();
            return new ClusterAgreementThresholdRow(
                t,
                c.Count, correct.Count == 0 ? 0 : (double)c.Count / correct.Count, c.Sum(r => (long)r.Size),
                m.Count, mixed.Count == 0 ? 0 : (double)m.Count / mixed.Count, m.Sum(r => (long)r.Size));
        }).ToList();
    }

    private static readonly (string Label, int Min, int Max)[] Bands =
    [
        ("2", 2, 2),
        ("3-5", 3, 5),
        ("6-20", 6, 20),
        ("21-100", 21, 100),
        ("101-1000", 101, 1000),
        ("1001+", 1001, int.MaxValue)
    ];

    private static List<ClusterShapeSizeBandRow> BuildSizeBands(IReadOnlyList<ClusterShapeRow> rows)
    {
        var result = new List<ClusterShapeSizeBandRow>();
        foreach (var (label, min, max) in Bands)
            foreach (var verdict in Enum.GetValues<ClusterVerdict>())
            {
                var band = rows.Where(r => r.Verdict == verdict && r.Size >= min && r.Size <= max).ToList();
                if (band.Count == 0) continue;
                result.Add(new ClusterShapeSizeBandRow(
                    label, verdict, band.Count,
                    band.Count(r => r.SplitByOneEdge),
                    Median(band.Select(r => r.EdgesPerRecord)),
                    Median(band.Select(r => r.ComparedFraction)),
                    Median(band.Select(r => r.LargestTwoEdgeConnectedFraction)),
                    Median(band.Select(r => r.AgreementRate))));
            }
        return result;
    }

    /// <summary>Lower median on an even count — a reported value that some cluster actually has,
    /// rather than an average of two that sits between them.</summary>
    internal static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 0 : sorted[(sorted.Count - 1) / 2];
    }
}
