namespace Linkuity.Pipeline;

/// <summary>
/// How much a field is actually worth on THIS data, in the terms the decision needs.
/// <para>
/// The two rates are the whole story, and neither means anything alone. A field where the same
/// entity agrees 88% of the time looks excellent until you see that unrelated records agree 22% of
/// the time too — that is a city column, and agreeing on it barely narrows anything. A field is
/// useful when the two rates are FAR APART, which is what <see cref="Bits"/> measures.
/// </para>
/// </summary>
public sealed record FieldUsefulnessRow(
    string FieldName,
    /// <summary>Share of records carrying a real value — blanks and the field's declared
    /// sentinels both count as unfilled, because a field the matcher cannot read is not filled
    /// whatever the cell contains.</summary>
    double FillRate,
    long RecordsFilled,
    /// <summary>How often records of the SAME entity agree. Null when too few were observed.</summary>
    double? SameEntityAgreement,
    /// <summary>How often records of DIFFERENT entities agree anyway.</summary>
    double? ChanceAgreement,
    long SameEntityComparisons,
    long DifferentEntityComparisons)
{
    /// <summary>
    /// Evidence one agreement carries, in bits: log2(same / chance). Null when either rate could
    /// not be estimated, which is reported as such rather than as zero — "we could not measure
    /// this" and "this is worthless" are different answers.
    /// </summary>
    public double? Bits => SameEntityAgreement is { } m && ChanceAgreement is { } u && m > 0 && u > 0
        ? Math.Log2(m / u)
        : null;

    /// <summary>
    /// A reading of <see cref="Bits"/> for someone deciding what to put in a profile. These bands
    /// are PRESENTATION ONLY: nothing scores off them, no threshold consults them, and changing
    /// them cannot change a match. They exist so a reader who does not think in log-odds still
    /// reaches the right conclusion about a column.
    /// </summary>
    public string Verdict => Bits switch
    {
        null => "not measured",
        >= 6 => "very strong",
        >= 3 => "strong",
        >= 1.5 => "moderate",
        >= 0.5 => "weak",
        _ => "nearly useless"
    };
}

/// <summary>
/// Records this data CANNOT separate: two or more different real entities whose every matchable
/// field is identical under the profile's own normalization.
/// <para>
/// This is not a tolerance and never excuses a wrong merge — a pair nothing can distinguish must
/// not be merged, it belongs in review or apart. It is a data-collection figure: it says how much
/// of the corpus is unresolvable no matter how the profile is tuned, so effort goes into obtaining
/// a distinguishing field rather than into thresholds that cannot help.
/// </para>
/// </summary>
public sealed record IndistinguishableGroups(
    int Groups,
    long RecordsInvolved,
    int LargestGroupRecords,
    int LargestGroupEntities,
    /// <summary>Matchable fields that were unfilled on EVERY record of the largest group — usually
    /// the direct answer to "what would we need to collect to separate these".</summary>
    IReadOnlyList<string> LargestGroupUnfilledFields);

public sealed record FieldUsefulnessResult(
    int Records,
    int LabeledRecords,
    long SameEntityPairsObserved,
    IReadOnlyList<FieldUsefulnessRow> Fields,
    IndistinguishableGroups Indistinguishable);
