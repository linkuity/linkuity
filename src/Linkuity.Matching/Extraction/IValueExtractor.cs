namespace Linkuity.Matching.Extraction;

/// <summary>
/// Derives one field's value from another field's value, so a fact carried INSIDE a column can be
/// compared as evidence in its own right instead of being folded into — or discarded from — the
/// comparison of the column that carries it.
///
/// The motivating case is an organization's legal form. It lives in the trailing token of the
/// legal name ("ABB AG", "ABB B.V."), and name canonicalization strips it, because keeping it
/// would make "THE BOEING COMPANY" and "BOEING CO" score as different names. Stripping is right
/// for the name and wrong for the fact: two members of one corporate group at one address differ
/// ONLY in that token, and once it is gone nothing separates them. Extracting it into its own
/// field leaves name comparison exactly as it was and gives the legal form its own measured
/// weight, rather than choosing which of the two facts to sacrifice.
///
/// Implementations are pure and per-record: they see one value and never the corpus.
/// </summary>
public interface IValueExtractor
{
    /// <summary>The name a profile field's <c>extractor</c> property refers to.</summary>
    string Name { get; }

    /// <summary>
    /// The derived value, or an empty string when the source carries nothing to derive. Empty is
    /// the correct answer for "no legal form in this name" — it becomes an absent field, which the
    /// scorer reports as Missing rather than as agreement or disagreement. Returning a
    /// placeholder instead would manufacture agreement between every name that lacks the fact.
    /// </summary>
    string Extract(string sourceValue);
}
