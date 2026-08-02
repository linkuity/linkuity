namespace Linkuity.Matching;

/// <summary>
/// The scale a scoring strategy's output is expressed on. Thresholds are only meaningful
/// against a known scale: 0.9 is a demanding threshold on one and nonsense on another.
///
/// This exists so the engine stops assuming every score is a normalized similarity. It is not
/// assumed anywhere that a score lies in [0,1] simply because today's scorers produce that.
/// </summary>
public enum ScoreScale
{
    /// <summary>
    /// A normalized similarity in [0,1], where 1 means the compared fields agreed completely.
    /// Every scorer shipped today produces this.
    /// </summary>
    UnitInterval = 0,

    /// <summary>
    /// Additive evidence in log-odds. Unbounded in both directions and comparable only within a
    /// profile: each field contributes evidence for or against a match, and a field neither
    /// record populates contributes nothing rather than moving an average. Reserved for
    /// evidence scoring; no shipped scorer produces it yet.
    /// </summary>
    LogOdds = 1
}
