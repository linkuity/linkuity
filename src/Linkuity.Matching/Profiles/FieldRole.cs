namespace Linkuity.Matching.Profiles;

/// <summary>
/// Metadata-driven roles a field plays in matching. Composable via [Flags].
/// <see cref="Identifier"/> marks a strong identifier: an exact match on it is
/// decisive MDM evidence (the identifier-weighted scorer floors such a pair to
/// the auto band), and it produces an exact blocking key. An identifier field is
/// normally also <see cref="Matchable"/> (so a similarity signal is produced) and
/// <see cref="Blocking"/> (so candidates sharing it are retrieved).
/// </summary>
[Flags]
public enum FieldRole
{
    None = 0,
    Searchable = 1,
    Matchable = 2,
    Blocking = 4,
    Identifier = 8
}
