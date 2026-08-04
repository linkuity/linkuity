namespace Linkuity.Matching.Strategies;

/// <summary>
/// Why a field comparison produced the value it did. Before this existed, four different
/// situations arrived downstream as the same thing — absence — and a scorer could not tell a
/// field neither record populates from one an evaluator declined to judge.
/// </summary>
public enum ComparisonOutcome
{
    /// <summary>Both sides present, evaluator returned a value. Only here is Value meaningful.</summary>
    Compared = 0,

    /// <summary>One record has the field, the other does not.</summary>
    MissingOneSide,

    /// <summary>Neither record has it. Uninformative, as distinct from weakly informative.</summary>
    MissingBoth,

    /// <summary>Both present, evaluator declined to judge — two unparseable dates, say.</summary>
    NotComparable
}
