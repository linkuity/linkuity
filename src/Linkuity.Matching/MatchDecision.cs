namespace Linkuity.Matching;

/// <summary>
/// The three-band match outcome reproduced from the durable matcher's
/// auto-match / review / no-match decision bands.
/// </summary>
public enum MatchDecision
{
    NoMatch = 0,
    Review = 1,
    AutoMatch = 2
}
