using Linkuity.Core.Models;
using Linkuity.Core.Normalization;

namespace Linkuity.Matching.Profiles;

/// <summary>
/// Metadata-driven matching configuration selected per content type. Names
/// reference strategies registered in the <see cref="Strategies.IStrategyRegistry"/>.
///
/// A record so that callers needing a variant use <c>with</c> rather than hand-copying
/// every property. Hand-copies drift: the durable resolver's copy silently dropped
/// <see cref="MaxBlockSize"/>, which disabled block suppression on every durable
/// organization ingest without any configuration change or error.
/// </summary>
public sealed record MatchingProfile
{
    public required string ContentType { get; init; }
    public required IReadOnlyList<ProfileField> Fields { get; init; }

    public required string NormalizationStrategy { get; init; }
    public required IReadOnlyList<string> BlockingStrategies { get; init; }
    public required string CandidateRetrievalStrategy { get; init; }
    public required string SimilarityStrategy { get; init; }
    public required string ScoringStrategy { get; init; }
    public required string DecisionStrategy { get; init; }
    public required string ClusteringStrategy { get; init; }

    public required double AutoMatchThreshold { get; init; }
    public required double ReviewThreshold { get; init; }

    /// <summary>
    /// Minimum weighted per-field similarity a non-identifier candidate must reach for the review
    /// floor (0.80) to apply. Below this the raw weighted score stands (typically a NoMatch), which
    /// stops a shared low-cardinality blocking key alone from flooding the review queue. Default 0.75.
    /// Lower it below <see cref="ReviewThreshold"/> to promote strongly-evidenced sub-threshold pairs
    /// into review. Consumed by <c>IdentifierAwareWeightedScoringStrategy</c>.
    /// </summary>
    public double ReviewFloorGate { get; init; } = 0.75;

    /// <summary>
    /// Corroboration gate for the identifier floor: the minimum weighted per-field similarity a
    /// pair must reach on its own before a field carrying <see cref="FieldRole.Identifier"/>
    /// matching at 1.0 is allowed to floor it into the auto band (0.98). An identifier match
    /// promotes a plausible pair to auto; it does not rescue an implausible one. Below this the
    /// identifier floor does not apply and the pair falls through to the ordinary review-floor
    /// logic. Default 0.35 — measured, not chosen: on the FEBRL corpus it blocks all 313 bare
    /// date-of-birth false auto-merges while costing 0 true positives after clustering
    /// (<c>.superpowers/phase0/corroboration-separation.md</c>). Raise it to demand more
    /// corroboration for identifiers that are not truly unique to one entity (date of birth,
    /// shared switchboard phone, a domain shared by sibling companies); set it to 0 to restore
    /// the unconditional floor. Consumed by <c>IdentifierAwareWeightedScoringStrategy</c>.
    /// The value is a normalized weighted average over the fields BOTH records populate, so it is
    /// not portable across profiles of very different field shape — re-measure before reusing it.
    /// </summary>
    public double IdentifierFloorGate { get; init; } = 0.35;

    /// <summary>
    /// Optional blocking-key suppression threshold: keys whose block size EXCEEDS this value
    /// stop driving candidate generation (a block exactly at the threshold stays active).
    /// Unset (null): the indexed/Lucene path derives its threshold from MaxCandidates (a block
    /// bigger than what retrieval can return is already truncated), and the linear path applies
    /// no suppression (completeness is its contract). Set it comfortably above the largest
    /// legitimate same-entity cluster expected in the data — a threshold below that cuts true
    /// recall, not junk. Measure with `match blocking audit --max-block-size` before committing
    /// a value.
    /// </summary>
    public int? MaxBlockSize { get; init; }

    /// <summary>
    /// Two-letter region code used to interpret phone numbers written without a country code.
    /// Defaults to <c>US</c>, which is what the normalizer previously hardcoded — a default, not
    /// a correct answer. A feed of national numbers from anywhere else needs this set, or those
    /// numbers are either stamped as US or left un-normalized with nothing reported. Numbers that
    /// carry an explicit country code ignore it.
    /// </summary>
    public string DefaultPhoneRegion { get; init; } = FieldNormalizer.DefaultPhoneRegion;

    /// <summary>
    /// How to read a slash-separated date whose first two components could each be day or month.
    /// <c>03/04/1980</c> is 3 April under the default and 4 March under DayFirst, and nothing in
    /// the value says which. Reading a feed under the wrong order does not fail: dates with a day
    /// past the twelfth pass through unparsed while the rest parse to confidently wrong values.
    /// Set it for any feed that is not written US-style.
    /// </summary>
    public DateFieldOrder DefaultDateOrder { get; init; } = DateFieldOrder.MonthFirst;

    /// <summary>
    /// Values that carry no information despite being rare — "N/A", "UNKNOWN", "TEST". They keep
    /// their ordinary agreement evidence and lose only rarity weighting (stage 2).
    /// </summary>
    public IReadOnlyList<string> PlaceholderValues { get; init; } = [];

    /// <summary>
    /// Minimum share of the comparisons made INSIDE a cluster that must have agreed, or the
    /// cluster does not form. Default 0.50 — a simple majority, chosen by argument rather than by
    /// measurement: a cluster whose own comparisons disagree more often than they agree is
    /// self-contradictory on its face, and that holds without reference to any corpus. A customer
    /// with no ground truth cannot re-derive a threshold from their own data, so the default has to
    /// be defensible on its own terms. (On 1,052,432 labelled SEC records this also costs nothing —
    /// 11,786 correct clusters survive — but that measurement corroborates the default, it is not
    /// the reason for it.)
    /// </summary>
    public double MinClusterCohesion { get; init; } = 0.50;

    /// <summary>
    /// Optional backstop: a cluster larger than this does not form automatically. Null (off) by
    /// default, and deliberately so — 99.97% of correct SEC clusters hold five records or fewer,
    /// but that is a property of registrants having few name variants. A customer consolidating
    /// fifty source systems has large correct clusters, and a shared default would destroy them.
    /// </summary>
    public int? MaxAutoClusterSize { get; init; }

    /// <summary>
    /// Returns a copy of this profile with <see cref="CandidateRetrievalStrategy"/>
    /// replaced. Used by the batch run path to force blocking-gated retrieval, which
    /// the identifier-weighted scorer's review floor assumes.
    /// </summary>
    public MatchingProfile WithCandidateRetrievalStrategy(string strategy)
        => this with { CandidateRetrievalStrategy = strategy };

    /// <summary>
    /// This profile's thresholds as a validated pair. <paramref name="scale"/> is the scale of
    /// the scorer that will produce the scores being classified — the profile names its scoring
    /// strategy but does not resolve it, so a caller holding the registry supplies it. Defaults
    /// to the unit interval, which every scorer shipped today produces.
    /// </summary>
    public MatchThresholds ThresholdsOn(ScoreScale scale = ScoreScale.UnitInterval)
        => new(AutoMatchThreshold, ReviewThreshold, scale);

    /// <summary>
    /// Everything ingest-time normalization needs from this profile. Built here rather than at
    /// each call site so the batch and durable ingest paths cannot disagree about which fields
    /// get normalized or how bare phone numbers are interpreted.
    /// </summary>
    public NormalizationSettings NormalizationSettings()
        => new(
            Fields.ToDictionary(f => f.Name, f => f.SemanticType, StringComparer.OrdinalIgnoreCase),
            DefaultPhoneRegion,
            DefaultDateOrder);
}
