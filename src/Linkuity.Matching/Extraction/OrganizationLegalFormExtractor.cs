using Linkuity.Matching.Canonicalization;

namespace Linkuity.Matching.Extraction;

/// <summary>
/// The legal form carried by an organization name's trailing suffix, as an equivalence class:
/// "ABB AG" -> AG, "ABB B.V." -> BV, "THE BOEING COMPANY" -> CO, "BOEING CO" -> CO.
///
/// Alternative spellings of one form share a class, so the derived field agrees for
/// COMPANY/CO and disagrees for AG/BV. That is the whole point: name canonicalization must keep
/// stripping these tokens (or Boeing and Intel stop matching across sources), and this recovers
/// the one thing stripping destroys.
///
/// The OUTERMOST suffix is the legal form: "ACME HOLDINGS INC" is an INC. Where stripping would
/// peel several tokens, this reads only the last one, matching how the form is actually written.
///
/// A name that is nothing BUT a suffix ("INC") yields empty rather than INC, mirroring
/// <see cref="OrganizationNameCanonicalizer"/>'s refusal to strip below one token: the token is
/// serving as the name, so there is no separable legal form to compare.
/// </summary>
public sealed class OrganizationLegalFormExtractor : IValueExtractor
{
    private static readonly OrganizationNameCanonicalizer Canonicalizer = new();

    public string Name => "org-legal-form";

    public string Extract(string sourceValue)
    {
        if (string.IsNullOrWhiteSpace(sourceValue))
            return string.Empty;

        // Keeping suffixes is what makes the token visible at all — Canonicalize would already
        // have removed the one this reads.
        var tokens = Canonicalizer.CanonicalizeKeepingSuffixes(sourceValue);
        if (tokens.Count < 2)
            return string.Empty;

        return OrganizationNameCanonicalizer.LegalFormClass(tokens[^1]) ?? string.Empty;
    }
}
