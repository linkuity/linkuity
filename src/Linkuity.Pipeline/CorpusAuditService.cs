using Linkuity.Core.Models;
using Linkuity.Matching;
using Linkuity.Matching.Canonicalization;
using Linkuity.Matching.Clustering;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;

namespace Linkuity.Pipeline;

/// <summary>
/// Corpus-scale recall and precision audit. Unlike ScoringAuditService, which materializes every
/// candidate pair with a full per-field breakdown, this service is aggregate-only BY DEFAULT:
/// candidate pairs are owned by their lowest shared active key and emitted exactly once, with no
/// pair set retained. A pair set is materialized only when the profile configures a cluster merge
/// policy that could actually act on it (<see cref="MatchingProfile.MinClusterCohesion"/> or
/// <see cref="MatchingProfile.MaxAutoClusterSize"/>) — see the guard in <see cref="Audit"/>. See
/// docs/superpowers/specs/2026-07-28-missed-merge-detection-design.md.
/// </summary>
public sealed class CorpusAuditService
{
    private static readonly string[] SupportedScoring = ["weighted", "identifier-weighted", "evidence"];

    /// <summary>Stateless — every method reads only static vocabulary — so one shared instance
    /// is safe and keeps the audit on the matcher's own canonicalization.</summary>
    private static readonly OrganizationNameCanonicalizer Canonicalizer = new();

    private readonly IStrategyRegistry _registry;
    private readonly IClusterMergePolicy _mergePolicy;

    // clusterMergePolicy is OPTIONAL here, unlike IncrementalResolver's required constructor
    // parameter: that seam exists so a resolver can never be built with no policy by accident,
    // but this audit already has ~10 call sites (7 of them test fixtures asserting on the
    // default profile, where MinClusterCohesion/MaxAutoClusterSize are null and any policy
    // implementation behaves identically). Forcing all of them to thread a policy through would
    // be churn with no behavioural payoff. What matters is that production's OWN policy — not a
    // second, independently hardcoded instance — is what the audit consults when a profile could
    // actually exercise it, and that the CLI (the only caller that reports on real profiles) does
    // not lean on this default: see CorpusAuditCommands.cs, which passes one explicitly.
    public CorpusAuditService(IStrategyRegistry registry, IClusterMergePolicy? clusterMergePolicy = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _mergePolicy = clusterMergePolicy ?? new CohesionClusterMergePolicy();
    }

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
        var normalization = ProfileNormalization.Resolve(_registry, profile);
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

        // Asked of the INJECTED policy, not inlined here: which profile fields make rejection
        // possible is that policy's own knowledge (CohesionClusterMergePolicy.CanReject mirrors
        // its own Evaluate). Hardcoding CohesionClusterMergePolicy's specific null-checks in this
        // caller would silently strand a differently-behaved policy injected through the
        // constructor — the same hardcoding bug the constructor seam exists to close, one layer
        // down. Every profile shipped today makes CanReject false for the default cohesion policy,
        // so the audit stays aggregate-only — no pair set, matching the class doc above.
        var mergePolicyCanReject = _mergePolicy.CanReject(profile);

        // Every candidate pair the walk scores, kept only long enough to attribute each one to its
        // FINAL cluster below. Union-find keeps growing for the rest of the walk after this pair is
        // visited, so "did both endpoints land in the same cluster" is not decidable yet — the same
        // reason IncrementalResolver.AllComparisons is materialized in full before its own rollup,
        // rather than tallied against whatever root a record happens to have at visit time. Null,
        // not an empty list, when the policy cannot reject anything: allocating and filling
        // 11,000,007 entries (the SEC gate corpus) to feed a rollup with nothing to decide is the
        // exact cost this class exists to avoid.
        var comparisons = mergePolicyCanReject
            ? new List<(int Left, int Right, bool IsAuto)>(CandidatePairUpperBound(index, suppressed))
            : null;
        var occurrences = ForEachCandidatePair(index, effectiveMax, (l, r) =>
        {
            emitted++;
            var score = ScorePair(normalized, l, r, similarity, scoring, profile, out var comparable, out var lifted);
            if (lifted) floorLifted++;
            var band = BandOf(score, comparable, profile, scoring.Scale);
            var isAuto = band == CorpusBand.Auto;
            if (isAuto) uf.Union(l, r);
            comparisons?.Add((l, r, isAuto));
            if (truePairs.TryGetValue(Pack(l, r), out var state)) { state.Band = band; state.Score = score; }
        }, ct);

        var roots = new int[records.Count];
        for (var i = 0; i < records.Count; i++) roots[i] = uf.Find(i);

        // Mirrors production (IncrementalResolver.MaterializeComponent): a cluster whose own
        // comparisons contradict it too often is refused and dissolves into singletons. Must run
        // before SameCluster/ClusterPairCounts below so every number this audit reports — not just
        // the cluster summary — reflects the clustering production would actually have formed.
        var rejectedComponents = mergePolicyCanReject
            ? ApplyClusterMergePolicy(roots, comparisons!, profile, _mergePolicy)
            : [];

        foreach (var state in truePairs.Values) state.SameCluster = roots[state.Left] == roots[state.Right];

        // Spec §6.4/§9.7: computed from the SAME rejected-component list ApplyClusterMergePolicy
        // just dissolved, and the SAME byLabel grouping truePairs was built from above — never a
        // second, independently recomputed notion of "the true entity's full membership".
        var blastRadius = mergePolicyCanReject
            ? ComputeBlastRadius(rejectedComponents, trueLabel, byLabel)
            : null;

        var (tp, pp, ap) = ClusterPairCounts(roots, trueLabel);
        return BuildResult(records, profile, effectiveMax, normalized, trueLabel, roots,
            unlabeled, emitted, occurrences, floorLifted, truePairs.Values, tp, pp, ap, blastRadius, byLabel);
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
        IEnumerable<TruePairState> states, long tp, long pp, long ap, CohesionBlastRadius? blastRadius,
        IReadOnlyDictionary<string, List<int>> byLabel)
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
        // "Populated" is asked of ProfileField.IsAbsent, not a raw blank check, so a declared
        // sentinel (e.g. GLEIF legal_form "8888") is reported as absent here exactly as it is to
        // the matcher -- otherwise this coverage figure and the engine's own comparability
        // decision would silently disagree on the same field.
        var coverage = profile.Fields
            .Where(f => f.Roles.HasFlag(FieldRole.Matchable))
            .Select(f => new FieldCoverageRow(f.Name, f.Weight, all.Count(x =>
                normalized[x.Left].Fields.TryGetValue(f.Name, out var lv) && !f.IsAbsent(lv) &&
                normalized[x.Right].Fields.TryGetValue(f.Name, out var rv) && !f.IsAbsent(rv))))
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

        var overMerge = BuildOverMergeAudit(clusterSizes.Values, summary.LargestClusterSize, byLabel);

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
            counts, metrics, summary, strata, outcomes, overMerge,
            new WrongMergeGate(counts.PredictedPositive, counts.TruePositive),
            blastRadius);
    }

    /// <summary>
    /// The oracle is the largest true entity size IN THIS RUN'S OWN GROUND TRUTH — the largest
    /// number of records <paramref name="byLabel"/> groups under one canonical key — never a
    /// literal number, so the same code path is exactly as correct on a person corpus as on an
    /// organization one. Zero ground truth (an empty <paramref name="byLabel"/>) is vacuously
    /// passing: there is no oracle to measure a cluster against, so nothing can be reported as
    /// exceeding it. <paramref name="clusterSizes"/> is O(clusters), never O(pairs), so this scales
    /// to the multi-million-record corpora this service already targets.
    /// </summary>
    internal static OverMergeAudit BuildOverMergeAudit(
        IReadOnlyCollection<int> clusterSizes, int largestClusterSize,
        IReadOnlyDictionary<string, List<int>> byLabel)
    {
        if (byLabel.Count == 0) return new OverMergeAudit(0, largestClusterSize, 0, 0, 0);

        var oracle = byLabel.Values.Max(members => members.Count);
        var clustersOverOracle = 0;
        var recordsOverOracle = 0L;
        var clustersOverOneThousand = 0;
        foreach (var size in clusterSizes)
        {
            if (size > oracle) { clustersOverOracle++; recordsOverOracle += size; }
            if (size > 1000) clustersOverOneThousand++;
        }

        return new OverMergeAudit(oracle, largestClusterSize, clustersOverOracle, recordsOverOracle,
            clustersOverOneThousand);
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

    // Forwarders onto BlockingKeyIndex: CorpusAuditService and BlockingAuditService share exactly
    // ONE implementation of key generation, suppression and candidate-pair enumeration now — see
    // BlockingKeyIndex.cs. Kept here (rather than requiring every call site to be re-qualified)
    // so existing internal call sites in this class and existing external callers/tests keep
    // compiling unchanged.

    internal static KeyIndex BuildIndex(
        IReadOnlyList<EntityRecord> records, MatchingProfile profile, IStrategyRegistry registry,
        CancellationToken ct = default)
        => BlockingKeyIndex.Build(records, profile, registry, ct);

    internal static bool[] SuppressedKeys(KeyIndex index, int? maxBlockSize)
        => BlockingKeyIndex.SuppressedKeys(index, maxBlockSize);

    internal static int CandidatePairUpperBound(KeyIndex index, bool[] suppressed)
        => BlockingKeyIndex.CandidatePairUpperBound(index, suppressed);

    internal static long ForEachCandidatePair(
        KeyIndex index, int? maxBlockSize, Action<int, int> onPair, CancellationToken ct = default)
        => BlockingKeyIndex.ForEachCandidatePair(index, maxBlockSize, onPair, ct);

    internal static int LowestSharedActiveKey(int[] left, int[] right, bool[] suppressed)
        => BlockingKeyIndex.LowestSharedActiveKey(left, right, suppressed);

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
        // Signal PRESENCE stopped meaning "a comparison happened" once outcomes arrived: the
        // similarity strategy now emits one signal per matchable field even when neither side
        // populates it. Count > 0 would be permanently true for any profile with matchable
        // fields, so comparability must be asked of the outcomes, not the collection size.
        comparable = forwardSignals.Any(s => s.Outcome == ComparisonOutcome.Compared)
                     || reverseSignals.Any(s => s.Outcome == ComparisonOutcome.Compared);
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
    /// invalidate every recorded comparison. <paramref name="scale"/> is required rather than
    /// defaulted to UnitInterval: a defaulted scale here previously let a call site compile and
    /// pass its tests while silently assuming the unit interval, a defect found and fixed five
    /// times across this programme (twice in this class alone). Callers must pass the resolved
    /// scorer's own scale so a LogOdds scorer's thresholds are validated against LogOdds, not
    /// against [0,1].
    /// </summary>
    internal static CorpusBand BandOf(
        double score, bool comparable, MatchingProfile profile, ScoreScale scale)
        => MatchBandClassifier.Classify(score, comparable, profile.ThresholdsOn(scale)) switch
        {
            MatchDecision.AutoMatch => CorpusBand.Auto,
            MatchDecision.Review => CorpusBand.Review,
            MatchDecision.NonComparable => CorpusBand.NonComparable,
            _ => CorpusBand.NoMatch
        };

    private static long Choose2(long n) => n * (n - 1) / 2;

    /// <summary>A component the merge policy refused, captured with its pre-dissolution membership
    /// so <see cref="ComputeBlastRadius"/> can ask what was inside it. The members list is the
    /// SAME list <see cref="ApplyClusterMergePolicy"/> used to evaluate the policy — never
    /// recomputed from the (by then dissolved) <c>roots</c> array.</summary>
    internal sealed record RejectedComponent(IReadOnlyList<int> Members, ClusterMergeVerdict Verdict);

    /// <summary>
    /// Evaluates every multi-record root the walk produced through <paramref name="mergePolicy"/>
    /// — the SAME policy instance <see cref="Audit"/> was given, never one hardcoded here, so a
    /// production policy swap or decoration is guaranteed to be reflected in what this audit
    /// reports rather than silently left behind — using the SAME agreement definition: an
    /// agreement is an auto-band comparison, and the denominator is every comparison made between
    /// two members of that root's FINAL cluster (<paramref name="comparisons"/>, filtered to pairs
    /// whose endpoints share a root — never a second candidate-pair walk). A root the policy
    /// refuses has every member reset to its own root, in place, before returning — the audit's
    /// existing representation of "unclustered" — so the cluster metrics computed after this call
    /// see singletons rather than a cluster production would never have formed. Returns every
    /// refused component (with the membership it had BEFORE dissolution) so a caller can measure
    /// what the rejection destroyed, spec §6.4.
    /// </summary>
    private static List<RejectedComponent> ApplyClusterMergePolicy(
        int[] roots, IReadOnlyList<(int Left, int Right, bool IsAuto)> comparisons, MatchingProfile profile,
        IClusterMergePolicy mergePolicy)
    {
        var membersByRoot = new Dictionary<int, List<int>>();
        for (var i = 0; i < roots.Length; i++)
            (membersByRoot.TryGetValue(roots[i], out var list) ? list : membersByRoot[roots[i]] = []).Add(i);

        var tally = new Dictionary<int, (long Comparisons, long Agreements)>();
        foreach (var (left, right, isAuto) in comparisons)
        {
            var root = roots[left];
            if (root != roots[right]) continue; // endpoints landed in different final clusters
            var (cmp, agr) = tally.GetValueOrDefault(root);
            tally[root] = (cmp + 1, agr + (isAuto ? 1 : 0));
        }

        var rejected = new List<RejectedComponent>();
        foreach (var (root, members) in membersByRoot)
        {
            if (members.Count < 2) continue; // singleton: no cluster for the policy to refuse
            var (cmp, agr) = tally.GetValueOrDefault(root);
            // Members comes from the component's own membership list, never from
            // default(ClusterEvidenceCounts) — that reads as a fully-agreeing zero-member cluster.
            var counts = new ClusterEvidenceCounts(members.Count, cmp, agr);
            var verdict = mergePolicy.Evaluate(counts, profile);
            if (verdict == ClusterMergeVerdict.Accepted) continue;

            rejected.Add(new RejectedComponent(members, verdict));
            foreach (var member in members) roots[member] = member;
        }
        return rejected;
    }

    /// <summary>
    /// Spec §6.4/§9.7: the accepted design is reject-wholesale — a component that fails cohesion
    /// dissolves to singletons IN FULL, taking any correct sub-grouping with it. This asks, for
    /// each rejected component, whether a true entity's ENTIRE membership (every record ground
    /// truth assigns that label, corpuswide — <paramref name="byLabel"/>) happened to land inside
    /// it. Such a sub-grouping is not a heuristic guess at "correct" — every member of the real
    /// entity is present and nothing else carries its label, so on its own it would have been a
    /// perfectly correct cluster. Restricted to <see cref="ClusterMergeVerdict.RejectedForCohesion"/>
    /// because this is a cohesion measurement: <c>MaxAutoClusterSize</c> stays off throughout
    /// stage 1b, but a caller that turned it on should not have its rejections silently folded in
    /// here as if cohesion had caused them. A true label with fewer than two members corpuswide is
    /// already a singleton and has nothing to lose, so it is not counted.
    /// </summary>
    internal static CohesionBlastRadius ComputeBlastRadius(
        IReadOnlyList<RejectedComponent> rejectedComponents,
        string?[] trueLabel,
        IReadOnlyDictionary<string, List<int>> byLabel)
    {
        long rejectedForCohesion = 0, componentsWithLoss = 0, clustersLost = 0, recordsLost = 0;

        foreach (var component in rejectedComponents)
        {
            if (component.Verdict != ClusterMergeVerdict.RejectedForCohesion) continue;
            rejectedForCohesion++;

            var memberSet = new HashSet<int>(component.Members);
            var seenLabels = new HashSet<string>(StringComparer.Ordinal);
            var lostHere = 0;
            foreach (var i in component.Members)
            {
                if (trueLabel[i] is not { } label || !seenLabels.Add(label)) continue;
                var fullMembership = byLabel[label];
                if (fullMembership.Count < 2 || !fullMembership.TrueForAll(memberSet.Contains)) continue;
                lostHere++;
                recordsLost += fullMembership.Count;
            }
            if (lostHere == 0) continue;
            componentsWithLoss++;
            clustersLost += lostHere;
        }

        return new CohesionBlastRadius(rejectedForCohesion, componentsWithLoss, clustersLost, recordsLost);
    }

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
            "scoringStrategy", profile.ScoringStrategy, "weighted, identifier-weighted or evidence");
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
