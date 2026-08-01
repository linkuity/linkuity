namespace Linkuity.Matching;

/// <summary>
/// The match outcome for a pair.
///
/// <see cref="NonComparable"/> is deliberately distinct from <see cref="NoMatch"/>: the first
/// means nothing could be compared, the second that a comparison was made and the evidence was
/// insufficient. Only the audit tooling used to draw that line; production reported both as
/// no-match, which asserts a conclusion the evidence does not support.
///
/// Existing numeric values are unchanged so persisted data keeps its meaning. NonComparable is
/// appended rather than inserted for the same reason.
/// </summary>
public enum MatchDecision
{
    NoMatch = 0,
    Review = 1,
    AutoMatch = 2,

    /// <summary>
    /// No signal was produced for the pair — the two records share no field that both populate —
    /// so the score carries no information about them. Such a pair never becomes an edge, since
    /// it cannot clear a threshold; this exists so that fact can be stated rather than inferred.
    /// </summary>
    NonComparable = 3
}
