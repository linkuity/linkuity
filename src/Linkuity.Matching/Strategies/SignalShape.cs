namespace Linkuity.Matching.Strategies;

/// <summary>
/// The shape of the signals a similarity strategy produces and a scoring strategy consumes.
/// Pairing strategies that disagree produces a number rather than an error, which is why this
/// is declared rather than inferred.
/// </summary>
public enum SignalShape
{
    /// <summary>
    /// One signal per profile field, named for that field, valued as a similarity in [0,1].
    /// A scorer can look up the field's weight and evaluator by the signal's name.
    /// </summary>
    PerField = 0,

    /// <summary>
    /// Whole-record aggregates whose names are fixed by the strategy rather than drawn from the
    /// profile — shared blocking-key counts, exact-match flags, token overlap. Values are not
    /// all similarities: a shared-key count is a raw tally that happens to be a double. Feeding
    /// these to a per-field scorer yields no weight lookups and a meaningless score.
    /// </summary>
    Aggregate = 1
}
