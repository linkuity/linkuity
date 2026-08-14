using Linkuity.Matching.Strategies;

namespace Linkuity.Pipeline;

/// <summary>Band classification of a scored pair under the effective thresholds.</summary>
public enum ScoreBand
{
    Auto,
    Review,
    NoMatch,
    /// <summary>Zero similarity signals (no field comparable on both sides) — not a scored rejection.</summary>
    NonComparable,
    /// <summary>True pair blocking never reached (ground truth only). Its offline score is diagnostic.</summary>
    Unreachable
}

/// <summary>
/// One unordered pair (canonicalized: LeftSourceRecordId &lt; RightSourceRecordId ordinal).
/// Reachable pairs carry Score/EngineBand; unreachable true pairs carry
/// OfflineScore/WouldBeBand instead (EngineBand = Unreachable). IsTrue is null when
/// either endpoint is unlabeled.
/// </summary>
public sealed record ScoredPair(
    string LeftSourceRecordId,
    string RightSourceRecordId,
    bool Reachable,
    bool Comparable,
    double? Score,
    double? OfflineScore,
    ScoreBand EngineBand,
    ScoreBand? WouldBeBand,
    bool? IsTrue,
    IReadOnlyList<ScoreContribution> Breakdown)
{
    /// <summary>Sort/diagnostic key: the engine score when reachable, else the offline score.</summary>
    public double? EffectiveScore => Score ?? OfflineScore;
}

/// <summary>Candidate-pair counts per band (unreachable pairs are not candidates and not counted here).</summary>
public sealed record ScoringBandCounts(int Auto, int Review, int NoMatch, int NonComparable);

/// <summary>Ground-truth/input overlap accounting (spec: coverage line).</summary>
public sealed record ScoringCoverage(
    int RecordCount,
    int LabeledRecordCount,
    int SkippedGroundTruthRows,
    int UnlabeledEndpointPairs);

/// <summary>
/// Direct-edge metric family over labeled pairs. Null metric = n/a (zero denominator).
/// <para>
/// F1 is deliberately absent. It weights precision and recall equally, which is the wrong
/// objective when a wrong merge is not an acceptable trade for a found one: an F1 of 0.30 reads as
/// a tuning dial when it is in fact two thirds of merges being wrong. What replaces it is the pair
/// of numbers a reviewer actually needs — precision at the auto threshold, and how much recall the
/// review queue recovers on top of it, at what queue cost.
/// </para>
/// </summary>
public sealed record ScoringMetrics(
    int LabeledCandidatePairs,
    int TruePairs,
    int PredictedPositives,
    int TruePositives,
    double? Precision,
    double? Recall,
    /// <summary>Share of true pairs that were NOT auto-matched but did land in review.</summary>
    double? ReviewCapture,
    /// <summary>Labeled candidate pairs sitting in the review band — the queue's size, which is
    /// what recall recovered through review actually costs.</summary>
    int ReviewPairs,
    /// <summary>Share of true pairs reached by auto OR review. The honest coverage figure: recall
    /// alone understates a system whose design routes ambiguity to a human rather than merging
    /// it.</summary>
    double? RecallIncludingReview);

/// <summary>Every true pair attributed to exactly one outcome.</summary>
public sealed record MissDecomposition(
    int TruePairs,
    int AutoMatched,
    int Unreachable,
    int NonComparable,
    int InReview,
    int BelowReview);

/// <summary>One score-cut what-if row. Cuts are the distinct observed candidate scores plus the effective auto threshold.</summary>
public sealed record ThresholdSweepRow(
    double Cut,
    int PredictedPositives,
    int TruePositives,
    double? Precision,
    double? Recall,
    bool IsEffectiveThreshold);

/// <summary>Full result of a scoring audit over a record set for one profile.</summary>
public sealed record ScoringAuditResult(
    int RecordCount,
    string SimilarityStrategyName,
    string ScoringStrategyName,
    double EffectiveAutoThreshold,
    double EffectiveReviewThreshold,
    bool ThresholdsOverridden,
    int? MaxBlockSize,
    IReadOnlyList<ScoredPair> Pairs,
    ScoringBandCounts Bands,
    ScoringCoverage? Coverage,
    ScoringMetrics? Metrics,
    MissDecomposition? Misses,
    IReadOnlyList<ThresholdSweepRow> Sweep,
    IReadOnlyList<ScoredPair> TrueBelowAuto,
    IReadOnlyList<ScoredPair> FalseAtOrAboveReview);
