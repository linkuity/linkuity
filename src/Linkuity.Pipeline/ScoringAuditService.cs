using Linkuity.Core.Models;
using Linkuity.Matching.Profiles;
using Linkuity.Matching.Strategies;

namespace Linkuity.Pipeline;

/// <summary>
/// Analyzes scoring quality for a matching profile over a record set: candidate pairs
/// under the batch blocking-linear path's engine-parity suppression rule, band
/// outcomes, and (with ground truth) exact direct-edge P/R/F1, a threshold sweep over
/// distinct observed scores, miss decomposition, and per-field diagnostics. Pure and
/// I/O-free; the CLI supplies records and ground truth. Fidelity scope is the batch
/// path ONLY (BatchMatchingService force-rewrites retrieval to blocking-linear);
/// durable/Lucene retrieval is not modeled. Normalization is applied to both sides and
/// blocking keys are generated from normalized records, matching the batch path only
/// under identity normalization; non-identity profiles are rejected (enforced v1 guard).
/// See docs/superpowers/specs/2026-07-26-scoring-audit-instrument-design.md.
/// </summary>
public sealed class ScoringAuditService
{
    private static readonly string[] SupportedScoring = ["weighted", "identifier-weighted"];
    private const string RequiredSimilarity = "field-weighted";

    private readonly IStrategyRegistry _registry;
    private readonly BlockingAuditService _blockingAudit;

    public ScoringAuditService(IStrategyRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _blockingAudit = new BlockingAuditService(registry);
    }

    public ScoringAuditResult Audit(
        IReadOnlyList<EntityRecord> records,
        MatchingProfile profile,
        IReadOnlyDictionary<string, string>? groundTruth = null,
        int? maxBlockSize = null,
        double? autoThresholdOverride = null,
        double? reviewThresholdOverride = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(profile);

        if (!string.Equals(profile.SimilarityStrategy, RequiredSimilarity, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Scoring audit v1 requires similarityStrategy '{RequiredSimilarity}' (profile has " +
                $"'{profile.SimilarityStrategy}'): per-field breakdowns assume field-named signals.");
        if (!SupportedScoring.Contains(profile.ScoringStrategy, StringComparer.Ordinal))
            throw new ArgumentException(
                "Scoring audit v1 requires scoringStrategy 'weighted' or 'identifier-weighted' " +
                $"(profile has '{profile.ScoringStrategy}').");
        if (!string.Equals(profile.NormalizationStrategy, "identity", StringComparison.Ordinal))
            throw new ArgumentException(
                "Scoring audit v1 requires normalizationStrategy 'identity' (profile has " +
                $"'{profile.NormalizationStrategy}'): the audit normalizes both sides and keys, " +
                "while the batch path normalizes only the query side — non-identity profiles " +
                "would silently diverge from the batch-fidelity claim.");

        var duplicate = records
            .GroupBy(r => r.SourceRecordId, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Duplicate SourceRecordId in input: '{duplicate.Key}'.");

        var auto = autoThresholdOverride ?? profile.AutoMatchThreshold;
        var review = reviewThresholdOverride ?? profile.ReviewThreshold;
        if (!(review >= 0 && review < auto && auto <= 1))
            throw new ArgumentException(
                $"Thresholds must satisfy 0 <= review < auto <= 1 (auto={auto}, review={review}).");
        var overridden = autoThresholdOverride is not null || reviewThresholdOverride is not null;

        // Normalize all records once for scoring. The batch path normalizes only the
        // QUERY side of each comparison and leaves the corpus side raw; under the
        // 'identity' normalization enforced above, the two behaviors are identical
        // (pinned by the batch-parity test). Non-identity profiles are rejected by
        // the guard above rather than silently diverging here.
        var normalization = _registry.Normalization[profile.NormalizationStrategy];
        var bySource = records
            .Select(r => normalization.Normalize(r, profile))
            .ToDictionary(r => r.SourceRecordId, StringComparer.Ordinal);

        // Raw blocks from the blocking audit (its own suppression not requested);
        // engine-parity suppression applied here: blocking-linear counts key frequency
        // over a corpus that EXCLUDES the query record, so a full block of size S has
        // per-query frequency S-1 and is suppressed iff S-1 > maxBlockSize.
        var blocking = _blockingAudit.Audit(records, profile, groundTruth: null,
            maxCandidates: null, maxBlockSize: null);
        var effectiveMax = maxBlockSize ?? profile.MaxBlockSize;

        var candidateIds = new HashSet<(string, string)>();
        foreach (var block in blocking.Blocks)
        {
            if (block.Size < 2) continue;
            if (effectiveMax is { } max && block.Size - 1 > max) continue;
            var ids = block.MemberSourceRecordIds;
            for (var i = 0; i < ids.Count; i++)
                for (var j = i + 1; j < ids.Count; j++)
                    candidateIds.Add(Canonical(ids[i], ids[j]));
        }

        var similarity = _registry.Similarity[profile.SimilarityStrategy];
        var scoring = _registry.Scoring[profile.ScoringStrategy];

        ScoredPair Score(string left, string right, bool reachable, bool? isTrue)
        {
            var signals = similarity.Evaluate(bySource[left], bySource[right], profile);
            var result = scoring.Score(signals, profile);
            var comparable = signals.Count > 0;
            var band = !comparable ? ScoreBand.NonComparable
                : result.FinalScore >= auto ? ScoreBand.Auto
                : result.FinalScore >= review ? ScoreBand.Review
                : ScoreBand.NoMatch;
            return reachable
                ? new ScoredPair(left, right, true, comparable, result.FinalScore, null, band, null, isTrue, result.Breakdown)
                : new ScoredPair(left, right, false, comparable, null, result.FinalScore,
                    ScoreBand.Unreachable, band, isTrue, result.Breakdown);
        }

        var truth = new TruthContext(groundTruth, bySource);
        var pairs = candidateIds
            .Select(p => Score(p.Item1, p.Item2, reachable: true, truth.IsTrue(p.Item1, p.Item2)))
            .ToList();

        // Ground-truth-only work (unreachable true pairs, metrics, sweep, diagnostics)
        // is completed in this file by Task 2's AnalyzeGroundTruth.
        var (coverage, metrics, misses, sweep, trueBelowAuto, falseHazards, unreachableTrue) =
            AnalyzeGroundTruth(records.Count, truth, candidateIds, pairs, Score, auto, review);
        pairs.AddRange(unreachableTrue);

        pairs = pairs
            .OrderBy(p => p.LeftSourceRecordId, StringComparer.Ordinal)
            .ThenBy(p => p.RightSourceRecordId, StringComparer.Ordinal)
            .ToList();

        var bands = new ScoringBandCounts(
            pairs.Count(p => p.EngineBand == ScoreBand.Auto),
            pairs.Count(p => p.EngineBand == ScoreBand.Review),
            pairs.Count(p => p.EngineBand == ScoreBand.NoMatch),
            pairs.Count(p => p.EngineBand == ScoreBand.NonComparable));

        return new ScoringAuditResult(
            records.Count, profile.SimilarityStrategy, profile.ScoringStrategy,
            auto, review, overridden, effectiveMax,
            pairs, bands, coverage, metrics, misses, sweep, trueBelowAuto, falseHazards);
    }

    public static (string, string) Canonical(string a, string b)
        => string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

    /// <summary>Ground-truth bookkeeping: which records are labeled, and pair truth lookups.</summary>
    private sealed class TruthContext
    {
        private readonly IReadOnlyDictionary<string, string>? _truth;
        private readonly IReadOnlyDictionary<string, EntityRecord> _present;

        public TruthContext(IReadOnlyDictionary<string, string>? truth,
            IReadOnlyDictionary<string, EntityRecord> present)
        {
            _truth = truth;
            _present = present;
        }

        public bool HasTruth => _truth is not null;

        public bool IsLabeled(string id) => _truth is not null && _truth.ContainsKey(id) && _present.ContainsKey(id);

        public bool? IsTrue(string left, string right)
        {
            if (_truth is null || !IsLabeled(left) || !IsLabeled(right)) return null;
            return string.Equals(_truth[left], _truth[right], StringComparison.Ordinal);
        }

        public int LabeledRecordCount => _truth?.Keys.Count(_present.ContainsKey) ?? 0;
        public int SkippedRows => _truth?.Keys.Count(id => !_present.ContainsKey(id)) ?? 0;

        /// <summary>Labeled records grouped by canonical key (groups of 1 contribute no pairs).</summary>
        public IEnumerable<List<string>> Groups()
        {
            if (_truth is null) yield break;
            var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var (id, canonical) in _truth)
            {
                if (!_present.ContainsKey(id)) continue;
                (groups.TryGetValue(canonical, out var list) ? list : groups[canonical] = []).Add(id);
            }
            foreach (var g in groups.Values.OrderBy(g => g[0], StringComparer.Ordinal))
            {
                g.Sort(StringComparer.Ordinal);
                yield return g;
            }
        }
    }

    private (ScoringCoverage?, ScoringMetrics?, MissDecomposition?, IReadOnlyList<ThresholdSweepRow>,
        IReadOnlyList<ScoredPair>, IReadOnlyList<ScoredPair>, IReadOnlyList<ScoredPair>)
        AnalyzeGroundTruth(int recordCount, TruthContext truth, HashSet<(string, string)> candidateIds,
            List<ScoredPair> pairs, Func<string, string, bool, bool?, ScoredPair> score,
            double auto, double review)
    {
        if (!truth.HasTruth)
            return (null, null, null, [], [], [], []);

        // 1. True pairs from labeled groups; offline-score the unreachable ones.
        var truePairIds = new HashSet<(string, string)>();
        foreach (var group in truth.Groups())
            for (var i = 0; i < group.Count; i++)
                for (var j = i + 1; j < group.Count; j++)
                    truePairIds.Add(Canonical(group[i], group[j]));

        var unreachableTrue = truePairIds
            .Where(p => !candidateIds.Contains(p))
            .OrderBy(p => p.Item1, StringComparer.Ordinal).ThenBy(p => p.Item2, StringComparer.Ordinal)
            .Select(p => score(p.Item1, p.Item2, false, true))
            .ToList();

        // 2. Labeled candidate pairs drive metrics and the sweep.
        var labeledCandidates = pairs.Where(p => p.IsTrue is not null).ToList();
        var unlabeledEndpointPairs = pairs.Count - labeledCandidates.Count;
        var coverage = new ScoringCoverage(
            RecordCount: recordCount,
            LabeledRecordCount: truth.LabeledRecordCount,
            SkippedGroundTruthRows: truth.SkippedRows,
            UnlabeledEndpointPairs: unlabeledEndpointPairs);

        static double? Ratio(int num, int den) => den == 0 ? null : (double)num / den;

        var predicted = labeledCandidates.Where(p => p.Comparable && p.EngineBand == ScoreBand.Auto).ToList();
        var tp = predicted.Count(p => p.IsTrue == true);
        var precision = Ratio(tp, predicted.Count);
        var recall = Ratio(tp, truePairIds.Count);
        double? f1 = precision is { } pr && recall is { } re && pr + re > 0
            ? 2 * pr * re / (pr + re) : null;

        var trueReachableNotAuto = labeledCandidates
            .Where(p => p.IsTrue == true && p.EngineBand != ScoreBand.Auto).ToList();
        var reviewCapture = Ratio(
            trueReachableNotAuto.Count(p => p.EngineBand == ScoreBand.Review),
            trueReachableNotAuto.Count);

        var metrics = new ScoringMetrics(
            labeledCandidates.Count, truePairIds.Count, predicted.Count, tp,
            precision, recall, f1, reviewCapture);

        // 3. Miss decomposition: every true pair exactly once.
        var trueCandidates = labeledCandidates.Where(p => p.IsTrue == true).ToList();
        var misses = new MissDecomposition(
            TruePairs: truePairIds.Count,
            AutoMatched: trueCandidates.Count(p => p.EngineBand == ScoreBand.Auto),
            Unreachable: unreachableTrue.Count,
            NonComparable: trueCandidates.Count(p => p.EngineBand == ScoreBand.NonComparable),
            InReview: trueCandidates.Count(p => p.EngineBand == ScoreBand.Review),
            BelowReview: trueCandidates.Count(p => p.EngineBand == ScoreBand.NoMatch));

        // 4. Sweep: distinct observed scores of labeled comparable reachable pairs,
        // plus the effective auto threshold. Offline scores never participate.
        var sweepPop = labeledCandidates.Where(p => p.Comparable && p.Score is not null).ToList();
        var cuts = sweepPop.Select(p => p.Score!.Value).Append(auto)
            .Distinct().OrderBy(c => c).ToList();
        var sweep = cuts.Select(cut =>
        {
            var pp = sweepPop.Where(p => p.Score!.Value >= cut).ToList();
            var stp = pp.Count(p => p.IsTrue == true);
            var sp = Ratio(stp, pp.Count);
            var sr = Ratio(stp, truePairIds.Count);
            double? sf = sp is { } a && sr is { } b && a + b > 0 ? 2 * a * b / (a + b) : null;
            return new ThresholdSweepRow(cut, pp.Count, stp, sp, sr, sf, cut == auto);
        }).ToList();

        // 5. Diagnostics (full ordered lists; the CLI applies --top).
        var trueBelowAuto = trueCandidates
            .Where(p => p.EngineBand != ScoreBand.Auto)
            .Concat(unreachableTrue)
            .OrderByDescending(p => p.EffectiveScore ?? -1)
            .ThenBy(p => p.LeftSourceRecordId, StringComparer.Ordinal)
            .ThenBy(p => p.RightSourceRecordId, StringComparer.Ordinal)
            .ToList();
        var falseHazards = labeledCandidates
            .Where(p => p.IsTrue == false && p.Comparable && p.Score >= review)
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.LeftSourceRecordId, StringComparer.Ordinal)
            .ThenBy(p => p.RightSourceRecordId, StringComparer.Ordinal)
            .ToList();

        return (coverage, metrics, misses, sweep, trueBelowAuto, falseHazards, unreachableTrue);
    }
}
