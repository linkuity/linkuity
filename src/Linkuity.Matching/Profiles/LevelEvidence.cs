namespace Linkuity.Matching.Profiles;

/// <summary>
/// What one COMPARISON LEVEL is worth, as the two rates that mean something rather than a
/// pre-computed weight — the same reasoning as <see cref="FieldEvidence"/>, one level down.
/// <para>
/// The difference that matters: <see cref="FieldEvidence"/> describes a field that either agrees
/// or does not, and interpolates between the two, which its own documentation calls a monotone
/// stand-in rather than a calibrated quantity. Levels are the principled replacement. They are
/// mutually exclusive and exhaustive, so m is the share of SAME-entity pairs that land in this
/// level and u the share of DIFFERENT-entity pairs that do, and the evidence is simply
/// log2(m/u). Nothing is interpolated and nothing is assumed monotone.
/// </para>
/// <para>
/// Unlike <see cref="FieldEvidence"/>, m below u is legal and expected. The bottom level of a
/// comparison is the one pairs reach when nothing matched; different entities land there far more
/// often than same entities do, so its evidence is negative. Forbidding m &lt;= u here — as
/// FieldEvidence rightly does for a whole field — would make it impossible to express
/// disagreement at all.
/// </para>
/// </summary>
public sealed record LevelEvidence
{
    /// <summary>m — share of SAME-entity pairs that land in this level.</summary>
    public required double SameEntityRate
    {
        get;
        init
        {
            RequireOpenUnitInterval(value, nameof(SameEntityRate));
            field = value;
        }
    }

    /// <summary>u — share of DIFFERENT-entity pairs that land in this level.</summary>
    public required double ChanceRate
    {
        get;
        init
        {
            RequireOpenUnitInterval(value, nameof(ChanceRate));
            field = value;
        }
    }

    /// <summary>
    /// Ceiling on this level's evidence, in bits. Applies only when the evidence is positive: a
    /// cap exists to stop one comparison carrying a merge by itself, which negative evidence never
    /// does. Required by the loader on every level that scores positive, for the same reason
    /// <see cref="FieldEvidence.MaxAgreementBits"/> is required off identifier fields.
    /// </summary>
    public double? MaxBits
    {
        get;
        init
        {
            if (value is { } cap && (double.IsNaN(cap) || double.IsInfinity(cap) || cap <= 0))
                throw new ArgumentOutOfRangeException(nameof(MaxBits), cap,
                    "maxBits must be a finite number greater than zero, or null on a level that scores negative.");
            field = value;
        }
    }

    /// <summary>
    /// Evidence contributed by a pair landing in this level. Positive when same-entity pairs reach
    /// it more often than chance, negative when they reach it less often.
    /// </summary>
    public double Bits
    {
        get
        {
            var raw = Math.Log2(SameEntityRate / ChanceRate);
            return raw > 0 && MaxBits is { } cap ? Math.Min(raw, cap) : raw;
        }
    }

    private static void RequireOpenUnitInterval(double value, string name)
    {
        if (double.IsNaN(value) || value <= 0 || value >= 1)
            throw new ArgumentOutOfRangeException(name, value,
                $"{name} must be strictly between 0 and 1; 0 or 1 produces infinite evidence.");
    }
}
