namespace Linkuity.Pipeline;

/// <summary>Full result of a blocking audit over a record set for one profile.</summary>
public sealed record BlockingAuditResult(
    int RecordCount,
    IReadOnlyList<string> StrategyNames,
    IReadOnlyList<RecordBlocking> PerRecord,
    IReadOnlyList<BlockingBlock> Blocks,
    BlockingStructuralStats Structural,
    IReadOnlyList<BlockingBlock> CapHazards,
    BlockingReachabilityReport? Reachability,
    BlockingSuppressionReport? Suppression = null);

/// <summary>One record's blocking keys, grouped by the strategy that produced them.</summary>
public sealed record RecordBlocking(
    string SourceRecordId,
    IReadOnlyDictionary<string, IReadOnlyList<string>> KeysByStrategy,
    IReadOnlyList<string> AllKeys);

/// <summary>A single blocking key and the records that share it.</summary>
public sealed record BlockingBlock(
    string Key,
    IReadOnlyList<string> StrategyNames,
    IReadOnlyList<string> MemberSourceRecordIds)
{
    public int Size => MemberSourceRecordIds.Count;
}

/// <summary>Aggregate structure of the blocking output (no ground truth needed).</summary>
public sealed record BlockingStructuralStats(
    int TotalBlocks,
    int SingletonRecordCount,
    int TotalCandidatePairs,
    int MaxBlockSize,
    double MeanBlockSize,
    IReadOnlyList<BlockingBlock> LargestBlocks);

/// <summary>Blocking recall ceiling and diagnostics against a ground-truth map.</summary>
public sealed record BlockingReachabilityReport(
    int TrueMatchPairs,
    int ReachablePairs,
    double Recall,
    IReadOnlyList<MissedPair> MissedPairs,
    IReadOnlyList<StrategyAttribution> Attribution);

/// <summary>A true-match pair that shares no blocking key (never compared).</summary>
public sealed record MissedPair(
    string LeftSourceRecordId,
    string RightSourceRecordId,
    string CanonicalKey,
    IReadOnlyList<string> LeftKeys,
    IReadOnlyList<string> RightKeys);

/// <summary>How many true-match pairs a strategy reaches, and how many only it reaches.</summary>
public sealed record StrategyAttribution(
    string StrategyName,
    int ReachablePairsContributed,
    int UniquelyReachablePairs);

/// <summary>
/// What frequency-aware suppression at MaxBlockSize would do to this record set: which
/// keys stop driving candidacy, which records are left with no active key (blocking
/// singletons), and the EFFECTIVE reachability computed over active keys only (null
/// without ground truth). Compare with the raw <see cref="BlockingReachabilityReport"/>
/// to read suppression's recall cost.
/// </summary>
public sealed record BlockingSuppressionReport(
    int MaxBlockSize,
    IReadOnlyList<BlockingBlock> SuppressedBlocks,
    IReadOnlyList<string> NoActiveKeyRecordIds,
    BlockingReachabilityReport? EffectiveReachability);
