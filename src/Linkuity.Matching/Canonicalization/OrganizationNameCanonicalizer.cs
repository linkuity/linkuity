using System.Text;
using System.Text.RegularExpressions;

namespace Linkuity.Matching.Canonicalization;

/// <summary>
/// Organization-name canonicalization for blocking: uppercase, period/apostrophe
/// deletion, ampersand-initial collapse (AT &amp; T -> ATT), punctuation folding,
/// leading-article drop, and repeated trailing legal-suffix stripping against a
/// curated international list. Returns empty only when the input has no alphanumeric content (blank or punctuation-only placeholders like "-"); suffix stripping itself never empties a real name.
/// </summary>
public sealed partial class OrganizationNameCanonicalizer : ITokenCanonicalizer
{
    private static readonly HashSet<string> LeadingArticles =
        new(StringComparer.Ordinal) { "THE", "A", "AN" };

    // Trailing-only legal suffixes, stripped repeatedly (ACME HOLDINGS INC -> ACME) but
    // never mid-name and never below one remaining token. GROUP/GRUPO/INTERNATIONAL/
    // GLOBAL are deliberately absent: semantic, not legal. DE participates only via
    // trailing repetition (SAB DE CV); BANCO DE CHILE is safe because stripping stops
    // at CHILE before ever seeing DE.
    //
    // The value is the suffix's EQUIVALENCE CLASS: tokens that are alternative spellings of
    // ONE legal form share a class (CO/COMPANY/COMPANIES, INC/INCORPORATED, LTD/LIMITED),
    // while genuinely different forms keep their own. That distinction is what
    // OrganizationLegalFormExtractor recovers after stripping has discarded the token, so
    // "ABB AG" and "ABB B.V." stay distinguishable while "BOEING CO" and "THE BOEING COMPANY"
    // do not become so. Forms that are easily mistaken for spelling variants but are separate
    // registrations are deliberately NOT merged: LP/LLP/LLLP, OY/OYJ (Finnish private vs
    // public), SA/SAS, KG/KGAA, LLC/PLLC.
    //
    // HOLDING(S) is a member for historical reasons and is semantic rather than legal; it is
    // left in place because removing it changes blocking reach, which is a separate,
    // separately-measured decision.
    private static readonly Dictionary<string, string> LegalSuffixClasses = new(StringComparer.Ordinal)
    {
        ["INC"] = "INC", ["INCORPORATED"] = "INC",
        ["CORP"] = "CORP", ["CORPORATION"] = "CORP",
        ["CO"] = "CO", ["COMPANY"] = "CO", ["COMPANIES"] = "CO",
        ["LTD"] = "LTD", ["LIMITED"] = "LTD",
        ["LLC"] = "LLC", ["LC"] = "LLC",
        ["GMBH"] = "GMBH", ["MBH"] = "GMBH",
        ["HOLDINGS"] = "HOLDINGS", ["HOLDING"] = "HOLDINGS",
        ["LP"] = "LP", ["LLP"] = "LLP", ["LLLP"] = "LLLP",
        ["PLC"] = "PLC", ["PC"] = "PC", ["PLLC"] = "PLLC", ["ULC"] = "ULC",
        ["AG"] = "AG", ["KG"] = "KG", ["KGAA"] = "KGAA",
        ["SA"] = "SA", ["SAS"] = "SAS", ["SE"] = "SE", ["SRL"] = "SRL", ["SPA"] = "SPA",
        ["NV"] = "NV", ["BV"] = "BV", ["CV"] = "CV", ["SAB"] = "SAB", ["DE"] = "DE",
        ["AB"] = "AB", ["ASA"] = "ASA", ["AS"] = "AS", ["OY"] = "OY", ["OYJ"] = "OYJ",
        ["KK"] = "KK", ["PTY"] = "PTY", ["PTE"] = "PTE", ["BHD"] = "BHD", ["SDN"] = "SDN"
    };

    /// <summary>
    /// Whether a token is one of the trailing legal-form suffixes this canonicalizer strips.
    /// Exposed so analysis tools classify names with the SAME vocabulary the matcher uses,
    /// without handing out a mutable set. Additive: canonicalization behaviour is unchanged.
    /// </summary>
    public static bool IsLegalSuffix(string token)
        => !string.IsNullOrEmpty(token) && LegalSuffixClasses.ContainsKey(token.ToUpperInvariant());

    /// <summary>
    /// The equivalence class of a trailing legal-form suffix, or null when the token is not one.
    /// Alternative spellings of the same form share a class, so a comparison over classes treats
    /// CO and COMPANY as agreement and AG and BV as disagreement.
    /// </summary>
    public static string? LegalFormClass(string token)
        => !string.IsNullOrEmpty(token) && LegalSuffixClasses.TryGetValue(token.ToUpperInvariant(), out var cls)
            ? cls
            : null;

    [GeneratedRegex(@"([A-Z0-9]+) *& *([A-Z0-9]+)")]
    private static partial Regex AmpersandJoin();

    public IReadOnlyList<string> Canonicalize(string value) => Core(value, stripSuffixes: true);

    /// <summary>
    /// The same pipeline with trailing-suffix stripping skipped — acronym generation needs
    /// suffix initials (the C in SBC comes from CORP, which Canonicalize strips).
    /// </summary>
    public IReadOnlyList<string> CanonicalizeKeepingSuffixes(string value) => Core(value, stripSuffixes: false);

    /// <summary>
    /// Primary tokens plus, when different, the hyphen-joined variant (hyphens deleted like
    /// periods, so WAL-MART tokenizes as WALMART). Consumers wanting reach (fingerprint,
    /// token keys) iterate variants; single-token consumers (phonetic) use Canonicalize.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> Variants(string value)
    {
        var primary = Canonicalize(value);
        if (string.IsNullOrWhiteSpace(value) || !value.Contains('-'))
            return [primary];

        var joined = Canonicalize(value.Replace("-", ""));
        return joined.Count == 0 || joined.SequenceEqual(primary, StringComparer.Ordinal)
            ? [primary]
            : [primary, joined];
    }

    private static List<string> Core(string value, bool stripSuffixes)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        var upper = value.ToUpperInvariant().Replace(".", "").Replace("'", "");
        upper = CollapseAmpersandInitials(upper);

        var tokens = Tokenize(upper);
        if (tokens.Count == 0)
            return [];

        DropLeadingArticle(tokens);
        if (stripSuffixes)
            StripTrailingSuffixes(tokens);
        return tokens;
    }

    // AT & T -> ATT, S&P -> SP, TEXAS A & M -> TEXAS AM. Requires a single-character
    // side, so PROCTER & GAMBLE falls through to ordinary punctuation folding.
    // Loops so chains (A & B & C) fully collapse.
    private static string CollapseAmpersandInitials(string upper)
    {
        while (true)
        {
            var collapsed = AmpersandJoin().Replace(upper, m =>
                m.Groups[1].Value.Length == 1 || m.Groups[2].Value.Length == 1
                    ? m.Groups[1].Value + m.Groups[2].Value
                    : m.Value);
            if (collapsed == upper)
                return collapsed;
            upper = collapsed;
        }
    }

    private static List<string> Tokenize(string upper)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var c in upper)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0)
            tokens.Add(current.ToString());
        return tokens;
    }

    // Drop one leading article when at least one token remains and the next token is a
    // real word (length >= 2): THE GAP INC -> GAP, but A O SMITH keeps its A.
    private static void DropLeadingArticle(List<string> tokens)
    {
        if (tokens.Count >= 2 && LeadingArticles.Contains(tokens[0]) && tokens[1].Length >= 2)
            tokens.RemoveAt(0);
    }

    private static void StripTrailingSuffixes(List<string> tokens)
    {
        while (tokens.Count > 1 && LegalSuffixClasses.ContainsKey(tokens[^1]))
            tokens.RemoveAt(tokens.Count - 1);
    }
}
