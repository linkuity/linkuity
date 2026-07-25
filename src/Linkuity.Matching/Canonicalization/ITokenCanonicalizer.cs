namespace Linkuity.Matching.Canonicalization;

/// <summary>
/// Canonicalizes a raw field value into the ordered token list blocking strategies
/// key on. Implementations are pure and per-record; they never see the corpus.
/// Blocking-only: FieldNormalizer and similarity scoring never use this form.
/// </summary>
public interface ITokenCanonicalizer
{
    /// <summary>Uppercase canonical tokens in name order. Empty only when the input has no alphanumeric content.</summary>
    IReadOnlyList<string> Canonicalize(string value);

    /// <summary>
    /// Canonical token lists for variant-sensitive consumers (e.g. hyphen handling):
    /// the primary Canonicalize list first, alternates only when they differ. The
    /// default is the single primary list, so single-variant canonicalizers need no code.
    /// </summary>
    IReadOnlyList<IReadOnlyList<string>> Variants(string value) => [Canonicalize(value)];
}
