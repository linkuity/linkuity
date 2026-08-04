namespace Linkuity.Matching.Profiles;

/// <summary>
/// What one field is worth as evidence, expressed as the two probabilities that mean something
/// rather than as a pair of pre-computed weights.
/// <para>
/// Storing m and u rather than the weights is deliberate: they constrain each other, so a field
/// cannot be given enormous agreement value and no disagreement penalty; they are arguable by a
/// human ("how often do two records for the same company agree on postcode?"); and stage 2
/// replaces u per value from a frequency table with nothing else in the design moving. Raw
/// weights would leave nothing for measurement to plug into.
/// </para>
/// </summary>
public sealed record FieldEvidence
{
    /// <summary>m — how often records of the SAME entity agree on this field.</summary>
    public required double SameEntityAgreement
    {
        get;
        init
        {
            RequireOpenUnitInterval(value, nameof(SameEntityAgreement));
            field = value;
        }
    }

    /// <summary>u — how often records of DIFFERENT entities agree on it by coincidence.</summary>
    public required double ChanceAgreement
    {
        get;
        init
        {
            RequireOpenUnitInterval(value, nameof(ChanceAgreement));
            field = value;
        }
    }

    /// <summary>
    /// Ceiling on agreement evidence, in bits. Null means uncapped, which is reserved for verified
    /// identifiers where "this alone is sufficient" is the field's purpose. Null must be a
    /// deliberate declaration rather than an omission — profile-load validation enforcing that is
    /// not yet in place and arrives with the cap rules.
    /// </summary>
    public double? MaxAgreementBits
    {
        get;
        init
        {
            if (value is { } cap && (double.IsNaN(cap) || double.IsInfinity(cap) || cap <= 0))
                throw new ArgumentOutOfRangeException(nameof(MaxAgreementBits), cap,
                    "maxAgreementBits must be a finite number greater than zero, or null for a verified identifier.");
            field = value;
        }
    }

    /// <summary>Evidence contributed by full agreement, after the cap.</summary>
    public double AgreementBits
    {
        get
        {
            Validate();
            var raw = Math.Log2(SameEntityAgreement / ChanceAgreement);
            return MaxAgreementBits is { } cap ? Math.Min(raw, cap) : raw;
        }
    }

    /// <summary>Evidence contributed by full disagreement. Negative, and NOT capped: a cap exists
    /// to stop one field carrying a merge on its own, which disagreement never does.</summary>
    public double DisagreementBits
    {
        get
        {
            Validate();
            return Math.Log2((1 - SameEntityAgreement) / (1 - ChanceAgreement));
        }
    }

    /// <summary>
    /// Evidence for a graded similarity, interpolated linearly between full disagreement and full
    /// agreement. This is a MONOTONE STAND-IN, not a calibrated quantity, and must not be
    /// described as one. The principled replacement is banded comparison levels with their own m
    /// and u, which can be introduced later without changing this signature.
    /// </summary>
    public double EvidenceFor(double similarity)
    {
        var low = DisagreementBits;
        return low + similarity * (AgreementBits - low);
    }

    /// <summary>
    /// Checked lazily rather than in a constructor because this is an init-only record: the two
    /// probabilities are set independently and neither setter can see the other's final value.
    /// </summary>
    private void Validate()
    {
        if (SameEntityAgreement <= ChanceAgreement)
            throw new ArgumentException(
                $"chanceAgreement ({ChanceAgreement}) must be below sameEntityAgreement ({SameEntityAgreement}); " +
                "otherwise agreeing on this field is evidence AGAINST the records being the same entity.");
    }

    private static void RequireOpenUnitInterval(double value, string name)
    {
        if (double.IsNaN(value) || value <= 0 || value >= 1)
            throw new ArgumentOutOfRangeException(name, value,
                $"{name} must be strictly between 0 and 1; 0 or 1 produces infinite evidence.");
    }
}
