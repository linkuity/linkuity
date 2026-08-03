using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Canonicalization;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;

namespace Linkuity.Pipeline;

/// <summary>
/// Corpus-scale recall and precision audit. Unlike ScoringAuditService, which materializes every
/// candidate pair with a full per-field breakdown, this service is aggregate-only and allocates
/// no pair set: candidate pairs are owned by their lowest shared active key and emitted exactly
/// once. See docs/superpowers/specs/2026-07-28-missed-merge-detection-design.md.
/// </summary>
public sealed class CorpusAuditService
{
    private static readonly string[] SupportedScoring = ["weighted", "identifier-weighted"];

    /// <summary>Interned blocking-key index. RecordKeys rows are ascending — Task 4's
    /// lowest-shared-key ownership rule needs it for a linear intersection scan.</summary>
    internal sealed record KeyIndex(int[][] RecordKeys, int[] KeyCount, int[][] KeyMembers, string[] KeyNames);

    private static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>Stateless — every method reads only static vocabulary — so one shared instance
    /// is safe and keeps the audit on the matcher's own canonicalization.</summary>
    private static readonly OrganizationNameCanonicalizer Canonicalizer = new();

    private readonly IStrategyRegistry _registry;

    public CorpusAuditService(IStrategyRegistry registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public CorpusAuditResult Audit(
        IReadOnlyList<EntityRecord> records,
        MatchingProfile profile,
        IReadOnlyDictionary<string, string> groundTruth,
        int? maxBlockSize = null,
        bool gateMode = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(groundTruth);

        ValidateSupportedStrategies(profile);

        var duplicate = records
            .GroupBy(r => r.SourceRecordId, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Duplicate SourceRecordId in input: '{duplicate.Key}'.");

        var effectiveMax = maxBlockSize ?? profile.MaxBlockSize;
        var normalization = _registry.Normalization[profile.NormalizationStrategy];
        var similarity = _registry.Similarity[profile.SimilarityStrategy];
        var scoring = _registry.Scoring[profile.ScoringStrategy];

        var indexOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < records.Count; i++) indexOf[records[i].SourceRecordId] = i;

        var trueLabel = new string?[records.Count];
        var unlabeled = 0;
        for (var i = 0; i < records.Count; i++)
            if (groundTruth.TryGetValue(records[i].SourceRecordId, out var label)) trueLabel[i] = label;
            else unlabeled++;

        ValidateCoverage(gateMode, records, groundTruth, indexOf, unlabeled);

        var normalized = records.Select(r => normalization.Normalize(r, profile)).ToArray();
        var index = BuildIndex(records, profile, _registry, ct);
        var suppressed = SuppressedKeys(index, effectiveMax);

        var byLabel = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < records.Count; i++)
            if (trueLabel[i] is { } l)
                (byLabel.TryGetValue(l, out var list) ? list : byLabel[l] = []).Add(i);

        var truePairs = new Dictionary<long, TruePairState>();
        foreach (var members in byLabel.Values)
            for (var i = 0; i < members.Count; i++)
                for (var j = i + 1; j < members.Count; j++)
                {
                    var lo = Math.Min(members[i], members[j]);
                    var hi = Math.Max(members[i], members[j]);
                    var leftRaw = RawName(records[lo], profile);
                    var rightRaw = RawName(records[hi], profile);
                    truePairs[Pack(lo, hi)] = new TruePairState(lo, hi,
                        ClassifyPair(Canonicalizer.Canonicalize(leftRaw), Canonicalizer.Canonicalize(rightRaw)),
                        LowestSharedActiveKey(index.RecordKeys[lo], index.RecordKeys[hi], suppressed) >= 0);
                }

        var uf = new UnionFind(records.Count);
        long emitted = 0, floorLifted = 0;
        var occurrences = ForEachCandidatePair(index, effectiveMax, (l, r) =>
        {
            emitted++;
            var score = ScorePair(normalized, l, r, similarity, scoring, profile, out var comparable, out var lifted);
            if (lifted) floorLifted++;
            var band = BandOf(score, comparable, profile);
            if (band == CorpusBand.Auto) uf.Union(l, r);
            if (truePairs.TryGetValue(Pack(l, r), out var state)) { state.Band = band; state.Score = score; }
        }, ct);

        var roots = new int[records.Count];
        for (var i = 0; i < records.Count; i++) roots[i] = uf.Find(i);
        foreach (var state in truePairs.Values) state.SameCluster = roots[state.Left] == roots[state.Right];

        var (tp, pp, ap) = ClusterPairCounts(roots, trueLabel);
        return BuildResult(records, profile, effectiveMax, normalized, trueLabel, roots,
            unlabeled, emitted, occurrences, floorLifted, truePairs.Values, tp, pp, ap);
    }

    private static long Pack(int lo, int hi) => ((long)lo << 32) | (uint)hi;

    /// <summary>
    /// The raw organization name the strata are computed from: the FIRST matchable
    /// OrganizationName field in profile field order. Profiles carrying more than one such
    /// field would need an explicit choice; today none do.
    /// </summary>
    private static string RawName(EntityRecord record, MatchingProfile profile)
    {
        foreach (var field in profile.Fields)
            if (field.SemanticType == SemanticFieldType.OrganizationName &&
                field.Roles.HasFlag(FieldRole.Matchable) &&
                record.Fields.TryGetValue(field.Name, out var v))
                return v;
        return "";
    }

    /// <summary>Name-similarity relationship between two canonical token lists (spec §8).</summary>
    internal static Stratum ClassifyPair(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var a = new HashSet<string>(left, StringComparer.Ordinal);
        var b = new HashSet<string>(right, StringComparer.Ordinal);
        if (a.Count == 0 || b.Count == 0) return Stratum.S5Disjoint;
        if (a.SetEquals(b)) return Stratum.S1Identical;

        var intersection = a.Count(b.Contains);
        if (intersection == 0) return Stratum.S5Disjoint;
        if (a.IsProperSubsetOf(b) || b.IsProperSubsetOf(a)) return Stratum.S2Containment;

        var union = a.Count + b.Count - intersection;
        return (double)intersection / union >= 0.5 ? Stratum.S3StrongOverlap : Stratum.S4WeakOverlap;
    }

    internal sealed class TruePairState(int left, int right, Stratum stratum, bool reachable)
    {
        public int Left { get; } = left;
        public int Right { get; } = right;
        public Stratum Stratum { get; } = stratum;
        public bool Reachable { get; } = reachable;
        public CorpusBand? Band { get; set; }
        public double? Score { get; set; }
        public bool SameCluster { get; set; }
    }

    private static CorpusAuditResult BuildResult(
        IReadOnlyList<EntityRecord> records, MatchingProfile profile, int? effectiveMax,
        EntityRecord[] normalized, string?[] trueLabel, int[] roots,
        int unlabeled, long emitted, long occurrences, long floorLifted,
        IEnumerable<TruePairState> states, long tp, long pp, long ap)
    {
        var all = states.ToList();

        var strata = Enum.GetValues<Stratum>().Select(s =>
        {
            var rows = all.Where(x => x.Stratum == s).ToList();
            return new CorpusStratumRow(s, rows.Count, rows.Count(x => x.Reachable),
                rows.Count(x => x.Band == CorpusBand.Auto), rows.Count(x => x.Band == CorpusBand.Review),
                rows.Count(x => x.Band == CorpusBand.NoMatch),
                rows.Count(x => x.Band == CorpusBand.NonComparable),
                rows.Count(x => x.SameCluster));
        }).ToList();

        // Field coverage over TRUE pairs: how often each matchable field was populated on both
        // sides, so the report shows the real denominator distribution rather than a scalar.
        var coverage = profile.Fields
            .Where(f => f.Roles.HasFlag(FieldRole.Matchable))
            .Select(f => new FieldCoverageRow(f.Name, f.Weight, all.Count(x =>
                normalized[x.Left].Fields.TryGetValue(f.Name, out var lv) && !string.IsNullOrWhiteSpace(lv) &&
                normalized[x.Right].Fields.TryGetValue(f.Name, out var rv) && !string.IsNullOrWhiteSpace(rv))))
            .ToList();

        long unlabeledEndpointPairs = 0;
        var clusterSizes = new Dictionary<int, int>();
        for (var i = 0; i < roots.Length; i++)
            clusterSizes[roots[i]] = clusterSizes.GetValueOrDefault(roots[i]) + 1;

        var labeledPerCluster = new Dictionary<int, int>();
        for (var i = 0; i < roots.Length; i++)
            if (trueLabel[i] is not null)
                labeledPerCluster[roots[i]] = labeledPerCluster.GetValueOrDefault(roots[i]) + 1;
        foreach (var (root, size) in clusterSizes)
        {
            var labeled = labeledPerCluster.GetValueOrDefault(root);
            unlabeledEndpointPairs += (long)labeled * (size - labeled) + Choose2(size - labeled);
        }

        var summary = new CorpusAuditClusterSummary(
            clusterSizes.Count,
            clusterSizes.Count == 0 ? 0 : clusterSizes.Values.Max(),
            clusterSizes.Values.Count(v => v > 1),
            clusterSizes.Values.Count(v => v == 1));

        var counts = new CorpusAuditCounts(records.Count, unlabeled, unlabeledEndpointPairs,
            all.Count, emitted, occurrences, ap, pp, tp,
            all.Count(x => x.Reachable), all.Count(x => x.Band == CorpusBand.Auto), floorLifted);

        double Ratio(long n, long d) => d == 0 ? 0.0 : (double)n / d;
        var metrics = new CorpusAuditMetrics(
            Ratio(counts.ReachableTruePairs, all.Count),
            Ratio(counts.DirectAutoTruePairs, all.Count),
            Ratio(tp, ap),
            Ratio(tp, pp));

        var outcomes = all
            .OrderBy(x => x.Left).ThenBy(x => x.Right)
            .Select(x => new TruePairOutcome(
                records[x.Left].SourceRecordId, records[x.Right].SourceRecordId,
                x.Stratum, x.Reachable, x.Band, x.Score, x.SameCluster))
            .ToList();

        return new CorpusAuditResult(
            new CorpusAuditInputs(effectiveMax, profile.AutoMatchThreshold, profile.ReviewThreshold,
                profile.ReviewFloorGate, coverage),
            counts, metrics, summary, strata, outcomes);
    }

    /// <summary>
    /// Gate mode requires the records ID set and the ground-truth ID set to be exactly equal.
    /// Cluster precision is undefined otherwise: an unlabeled record can transitively connect
    /// two labelled clusters, and neither ignoring nor penalizing that is defensible in a gate.
    /// </summary>
    private static void ValidateCoverage(
        bool gateMode, IReadOnlyList<EntityRecord> records,
        IReadOnlyDictionary<string, string> groundTruth,
        IReadOnlyDictionary<string, int> indexOf, int unlabeled)
    {
        if (!gateMode) return;

        if (unlabeled > 0)
        {
            var missing = records.Where(r => !groundTruth.ContainsKey(r.SourceRecordId))
                .Select(r => r.SourceRecordId).OrderBy(k => k, StringComparer.Ordinal).ToList();
            throw new ArgumentException(
                $"Gate mode requires every record to be labeled; {missing.Count} record(s) have no " +
                $"ground truth: {string.Join(", ", missing.Take(10))}{(missing.Count > 10 ? ", ..." : "")}.");
        }

        var absent = groundTruth.Keys.Where(k => !indexOf.ContainsKey(k))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (absent.Count > 0)
            throw new ArgumentException(
                $"Gate mode requires exact ID-set equality; {absent.Count} ground-truth row(s) name " +
                $"records absent from the corpus: {string.Join(", ", absent.Take(10))}" +
                $"{(absent.Count > 10 ? ", ..." : "")}.");
    }

    internal static KeyIndex BuildIndex(
        IReadOnlyList<EntityRecord> records, MatchingProfile profile, IStrategyRegistry registry,
        CancellationToken ct = default)
    {
        var normalization = registry.Normalization[profile.NormalizationStrategy];
        var keyIds = new Dictionary<string, int>(KeyComparer);
        var keyNames = new List<string>();
        var members = new List<List<int>>();
        var recordKeys = new int[records.Count][];

        for (var i = 0; i < records.Count; i++)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();

            var normalized = normalization.Normalize(records[i], profile);
            var ids = new SortedSet<int>();
            foreach (var strategyName in profile.BlockingStrategies)
                foreach (var key in registry.Blocking[strategyName].GenerateKeys(normalized, profile))
                {
                    if (!keyIds.TryGetValue(key, out var id))
                    {
                        id = keyNames.Count;
                        keyIds[key] = id;
                        keyNames.Add(key);
                        members.Add([]);
                    }
                    ids.Add(id);
                }

            recordKeys[i] = [.. ids];
            foreach (var id in ids) members[id].Add(i);
        }

        var keyMembers = new int[members.Count][];
        var keyCount = new int[members.Count];
        for (var k = 0; k < members.Count; k++)
        {
            keyMembers[k] = [.. members[k]];
            keyCount[k] = members[k].Count;
        }
        return new KeyIndex(recordKeys, keyCount, keyMembers, [.. keyNames]);
    }

    /// <summary>Engine parity: blocking-linear counts key frequency over a corpus that EXCLUDES
    /// the query record, so a block of size S has per-query frequency S-1 and is suppressed iff
    /// S-1 > maxBlockSize (BlockingAuditService.cs:118).</summary>
    internal static bool[] SuppressedKeys(KeyIndex index, int? maxBlockSize)
    {
        var suppressed = new bool[index.KeyCount.Length];
        if (maxBlockSize is not { } max) return suppressed;
        for (var k = 0; k < index.KeyCount.Length; k++)
            suppressed[k] = index.KeyCount[k] - 1 > max;
        return suppressed;
    }

    /// <summary>
    /// Emits each candidate pair EXACTLY ONCE with no deduplication structure: a pair is owned
    /// by the lowest active key id both records carry. Returns the OCCURRENCE count — how many
    /// block-pair visits happened — which is the real work measure and is always >= the number
    /// of emitted pairs.
    /// </summary>
    internal static long ForEachCandidatePair(
        KeyIndex index, int? maxBlockSize, Action<int, int> onPair, CancellationToken ct = default)
    {
        var suppressed = SuppressedKeys(index, maxBlockSize);
        long occurrences = 0;

        for (var k = 0; k < index.KeyMembers.Length; k++)
        {
            if (suppressed[k]) continue;
            var ids = index.KeyMembers[k];
            if (ids.Length < 2) continue;
            ct.ThrowIfCancellationRequested();

            for (var i = 0; i < ids.Length; i++)
                for (var j = i + 1; j < ids.Length; j++)
                {
                    occurrences++;
                    var a = ids[i];
                    var b = ids[j];
                    if (LowestSharedActiveKey(index.RecordKeys[a], index.RecordKeys[b], suppressed) == k)
                        onPair(a < b ? a : b, a < b ? b : a);
                }
        }
        return occurrences;
    }

    /// <summary>Lowest key id present in both ascending arrays and not suppressed; -1 if none.</summary>
    private static int LowestSharedActiveKey(int[] left, int[] right, bool[] suppressed)
    {
        int i = 0, j = 0;
        while (i < left.Length && j < right.Length)
        {
            if (left[i] < right[j]) i++;
            else if (left[i] > right[j]) j++;
            else
            {
                if (!suppressed[left[i]]) return left[i];
                i++; j++;
            }
        }
        return -1;
    }

    /// <summary>Union-find with path halving and union by size: 2 ints per record, so 1.05M
    /// records costs ~8MB.</summary>
    internal sealed class UnionFind
    {
        private readonly int[] _parent;
        private readonly int[] _size;

        public UnionFind(int count)
        {
            _parent = new int[count];
            _size = new int[count];
            for (var i = 0; i < count; i++) { _parent[i] = i; _size[i] = 1; }
        }

        public int Find(int x)
        {
            while (_parent[x] != x)
            {
                _parent[x] = _parent[_parent[x]];
                x = _parent[x];
            }
            return x;
        }

        public void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra == rb) return;
            if (_size[ra] < _size[rb]) (ra, rb) = (rb, ra);
            _parent[rb] = ra;
            _size[ra] += _size[rb];
        }
    }

    /// <summary>
    /// Scores one pair, taking the MAX of both directions exactly as the batch path does
    /// (BatchMatchingService.cs:63-73). For a symmetric evaluator this is a no-op; doing it
    /// unconditionally removes any need to assume symmetry.
    /// floorLifted is true when ANY floor raised the final score above the raw weighted average:
    /// identifier-weighted applies the 0.98 identifier floor on an exact identifier-field match
    /// and the 0.80 review floor when weighted sits in [ReviewFloorGate, 0.80). It is a general
    /// "a floor decided this band, not the similarity" flag, not a review-floor-only counter.
    /// </summary>
    internal static double ScorePair(
        EntityRecord[] normalized, int l, int r,
        ISimilarityStrategy similarity, IScoringStrategy scoring, MatchingProfile profile,
        out bool comparable, out bool floorLifted)
    {
        var forwardSignals = similarity.Evaluate(normalized[l], normalized[r], profile);
        var reverseSignals = similarity.Evaluate(normalized[r], normalized[l], profile);
        comparable = forwardSignals.Count > 0 || reverseSignals.Count > 0;
        if (!comparable) { floorLifted = false; return 0; }

        var forward = scoring.Score(forwardSignals, profile);
        var reverse = scoring.Score(reverseSignals, profile);
        var best = forward.FinalScore >= reverse.FinalScore ? forward : reverse;

        var weightedOnly = best.Breakdown.Sum(c => c.Contribution);
        floorLifted = best.FinalScore > weightedOnly + 1e-12;
        return best.FinalScore;
    }

    /// <summary>
    /// Maps the shared classifier onto this report's own enum. CorpusBand is kept rather than
    /// replaced because it is serialized into stored baselines: renaming or renumbering it would
    /// invalidate every recorded comparison.
    /// </summary>
    internal static CorpusBand BandOf(double score, bool comparable, MatchingProfile profile)
        => MatchBandClassifier.Classify(score, comparable, profile.ThresholdsOn()) switch
        {
            MatchDecision.AutoMatch => CorpusBand.Auto,
            MatchDecision.Review => CorpusBand.Review,
            MatchDecision.NonComparable => CorpusBand.NonComparable,
            _ => CorpusBand.NoMatch
        };

    private static long Choose2(long n) => n * (n - 1) / 2;

    /// <summary>
    /// Pair-counting cluster metrics from a contingency table — never by enumerating pairs inside
    /// a cluster. Memory is O(records). Computed over the LABELLED PROJECTION (spec §7): an
    /// unlabeled record contributes to neither predicted nor actual positives, but its presence
    /// in a cluster still connects the labelled records around it.
    /// </summary>
    internal static (long TruePositive, long PredictedPositive, long ActualPositive) ClusterPairCounts(
        int[] predictedRoot, string?[] trueLabel)
    {
        if (predictedRoot.Length != trueLabel.Length)
            throw new ArgumentException("predictedRoot and trueLabel must be the same length.");

        var labeledPerCluster = new Dictionary<int, long>();
        var trueSize = new Dictionary<string, long>(StringComparer.Ordinal);
        var cell = new Dictionary<(int, string), long>();

        for (var i = 0; i < predictedRoot.Length; i++)
        {
            if (trueLabel[i] is not { } label) continue;      // labelled projection
            labeledPerCluster[predictedRoot[i]] = labeledPerCluster.GetValueOrDefault(predictedRoot[i]) + 1;
            trueSize[label] = trueSize.GetValueOrDefault(label) + 1;
            var key = (predictedRoot[i], label);
            cell[key] = cell.GetValueOrDefault(key) + 1;
        }

        long tp = 0, pp = 0, ap = 0;
        foreach (var n in cell.Values) tp += Choose2(n);
        foreach (var n in labeledPerCluster.Values) pp += Choose2(n);
        foreach (var n in trueSize.Values) ap += Choose2(n);
        return (tp, pp, ap);
    }

    /// <summary>
    /// The configuration both corpus-scale audits model: the blocking-linear batch path. Shared so
    /// a second audit cannot drift into reporting on a pipeline neither of them implements.
    /// </summary>
    internal static void ValidateSupportedStrategies(MatchingProfile profile)
    {
        Require(profile.NormalizationStrategy == "identity", "normalizationStrategy",
            profile.NormalizationStrategy, "identity");
        Require(profile.SimilarityStrategy == "field-weighted", "similarityStrategy",
            profile.SimilarityStrategy, "field-weighted");
        Require(SupportedScoring.Contains(profile.ScoringStrategy, StringComparer.Ordinal),
            "scoringStrategy", profile.ScoringStrategy, "weighted or identifier-weighted");
        Require(profile.DecisionStrategy == "threshold", "decisionStrategy",
            profile.DecisionStrategy, "threshold");
        Require(profile.ClusteringStrategy == "union-find", "clusteringStrategy",
            profile.ClusteringStrategy, "union-find");
    }

    private static void Require(bool ok, string setting, string actual, string expected)
    {
        if (!ok)
            throw new ArgumentException(
                $"Corpus audit requires {setting} '{expected}' (profile has '{actual}'): the audit " +
                "models only the blocking-linear batch path and must not silently report on a " +
                "configuration it does not implement.");
    }
}
