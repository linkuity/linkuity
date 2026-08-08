namespace Linkuity.Pipeline;

/// <summary>Why the engine never compares a true pair. A and B are disjoint and exhaustive over
/// unreachable pairs; normalization loss is an orthogonal flag that may apply to either.</summary>
public sealed record ReachabilityDiagnosticResult(
    long TruePairs,
    long ReachablePairs,
    long UnreachablePairs,
    CauseTally CauseA,
    CauseTally CauseB1,
    CauseTally CauseB2,
    CauseTally CauseB3,
    NormalizationTally NormalizationImplicated,
    IReadOnlyList<SuppressedKeyDetail> CauseADetail,
    FieldCoOccurrenceSet Unreachable,
    ControlSet Control,
    BlockSizeHistogram Blocks);

/// <summary>A cause's pair count, plus a capped deterministic sample and, where the cause is
/// attributable to a column, the per-column breakdown.</summary>
public sealed record CauseTally(
    long PairCount,
    IReadOnlyDictionary<string, long> ByColumn,
    IReadOnlyList<SampledPair> Sample);

public sealed record NormalizationTally(
    long PairCount,
    long LegalSuffixOnlyPairCount,
    IReadOnlyList<SampledPair> Sample);

/// <summary>Cause A detail: which strategy owned the suppressed key, and how big the block was.
/// This is what a per-feature threshold would be chosen FROM — an aggregate count of cause A
/// tells you the cap hurt, not what to set it to.
///
/// Each bucket answers: if this strategy's threshold were raised above this block size, how many
/// currently-suppressed true pairs would become reachable? PairCount is deduped so a pair sharing
/// several keys from the same strategy at the same block size (common for multi-token names) is
/// counted once per bucket it touches, not once per key.
///
/// Buckets are NOT a partition of cause A and must NOT be summed: a pair whose shared keys sit at
/// DIFFERENT block sizes correctly appears in more than one bucket, since raising the threshold
/// past any one of them would recover that pair. Summing the buckets therefore double-counts such
/// pairs and can exceed <c>CauseA.PairCount</c> — that is not a reconciliation failure, it is the
/// buckets overlapping by construction. The pair total is always <c>CauseA.PairCount</c>.
/// </summary>
public sealed record SuppressedKeyDetail(string Strategy, int BlockSize, long PairCount);

public sealed record SampledPair(string LeftSourceRecordId, string RightSourceRecordId, string CanonicalKey);

/// <summary>Co-occurrence of a column's value across a pair. Rate without a sample size is
/// unreadable, and a rate without a control is unfalsifiable, so both ride along. Populated by
/// Task 4; Task 3 defines the shape only.</summary>
public sealed record FieldCoOccurrence(
    string Column, long SharedCount, long SampleSize, double Rate,
    double IntervalLow, double IntervalHigh, double? Lift);

public sealed record FieldCoOccurrenceSet(
    long SampledPairCount,
    IReadOnlyDictionary<string, FieldCoOccurrence> ByColumn);

/// <summary>Block-size distribution, computed from the interned index so it is bounded by bucket
/// count rather than corpus size.
///
/// This lives HERE and not only on BlockingAuditResult on purpose. BlockingAuditService retains
/// per-record and per-key string structures and is only run at sample scale; the full-corpus
/// histogram — which is what a per-feature frequency threshold would actually be chosen from, and
/// what Task 7 needs for the postcode probe — has to come from the service that runs at full
/// scale. That is this one.</summary>
public sealed record BlockSizeHistogram(
    IReadOnlyList<BlockingSizeBucket> Buckets,
    int TotalBlocks,
    int MaxBlockSize,
    IReadOnlyList<LargestBlock> Largest);

/// <summary>A single oversized block: its key, size, and emitting strategy. Capped to the top N —
/// this is how a toxic address or a stop-word name key is identified by NAME, not just counted.</summary>
public sealed record LargestBlock(string Key, string Strategy, int Size);

public sealed record ControlSet(
    long SampledPairCount,
    long TruePairsAccidentallyIncluded,
    IReadOnlyDictionary<string, FieldCoOccurrence> ByColumn);
