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
    private static readonly HashSet<string> LegalSuffixes = new(StringComparer.Ordinal)
    {
        "INC", "INCORPORATED", "CORP", "CORPORATION", "CO", "COMPANY", "COMPANIES",
        "LLC", "LC", "LP", "LLP", "LLLP", "LTD", "LIMITED", "PLC", "PC", "PLLC", "ULC",
        "HOLDINGS", "HOLDING",
        "GMBH", "MBH", "AG", "KG", "KGAA",
        "SA", "SAS", "SE", "SRL", "SPA",
        "NV", "BV", "CV", "SAB", "DE",
        "AB", "ASA", "AS", "OY", "OYJ",
        "KK", "PTY", "PTE", "BHD", "SDN"
    };

    [GeneratedRegex(@"([A-Z0-9]+) *& *([A-Z0-9]+)")]
    private static partial Regex AmpersandJoin();

    public IReadOnlyList<string> Canonicalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        var upper = value.ToUpperInvariant().Replace(".", "").Replace("'", "");
        upper = CollapseAmpersandInitials(upper);

        var tokens = Tokenize(upper);
        if (tokens.Count == 0)
            return [];

        DropLeadingArticle(tokens);
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
        while (tokens.Count > 1 && LegalSuffixes.Contains(tokens[^1]))
            tokens.RemoveAt(tokens.Count - 1);
    }
}
