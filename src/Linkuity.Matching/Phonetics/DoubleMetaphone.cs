using System.Text;

namespace Linkuity.Matching.Phonetics;

/// <summary>
/// Clean-room implementation of Lawrence Philips' Double Metaphone algorithm,
/// returning a primary and an alternate 4-character phonetic code. Used for
/// phonetic blocking so spelling variants of a name collapse to a shared key.
/// This is behaviorally faithful to the Python double-metaphone path used by the
/// legacy batch matcher; it is not guaranteed byte-identical to that package.
/// </summary>
public static class DoubleMetaphone
{
    private const int MaxLength = 4;

    public static (string Primary, string Alternate) Encode(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return ("", "");

        var word = new string(input.Where(char.IsLetter).ToArray()).ToUpperInvariant();
        if (word.Length == 0)
            return ("", "");

        var primary = new StringBuilder();
        var alternate = new StringBuilder();
        int length = word.Length;
        int current = 0;

        bool isSlavoGermanic =
            word.Contains('W') || word.Contains('K') ||
            word.Contains("CZ", StringComparison.Ordinal) ||
            word.Contains("WITZ", StringComparison.Ordinal);

        // Skip silent initial letters.
        if (At(word, 0, 2) is "GN" or "KN" or "PN" or "WR" or "PS")
            current = 1;

        // Initial 'X' is pronounced 'S' (e.g. "Xavier").
        if (word[0] == 'X')
        {
            Add(primary, alternate, "S");
            current = 1;
        }

        while (current < length && (primary.Length < MaxLength || alternate.Length < MaxLength))
        {
            char c = word[current];
            switch (c)
            {
                case 'A': case 'E': case 'I': case 'O': case 'U': case 'Y':
                    if (current == 0)
                        Add(primary, alternate, "A");
                    current += 1;
                    break;

                case 'B':
                    Add(primary, alternate, "P");
                    current += At(word, current + 1, 1) == "B" ? 2 : 1;
                    break;

                case 'Ç':
                    Add(primary, alternate, "S");
                    current += 1;
                    break;

                case 'C':
                    current = EncodeC(word, current, primary, alternate);
                    break;

                case 'D':
                    if (At(word, current, 2) == "DG")
                    {
                        if (IsOneOf(word, current + 2, 'I', 'E', 'Y'))
                        {
                            Add(primary, alternate, "J");
                            current += 3;
                        }
                        else
                        {
                            Add(primary, alternate, "TK");
                            current += 2;
                        }
                    }
                    else if (At(word, current, 2) is "DT" or "DD")
                    {
                        Add(primary, alternate, "T");
                        current += 2;
                    }
                    else
                    {
                        Add(primary, alternate, "T");
                        current += 1;
                    }
                    break;

                case 'F':
                    Add(primary, alternate, "F");
                    current += At(word, current + 1, 1) == "F" ? 2 : 1;
                    break;

                case 'G':
                    current = EncodeG(word, current, isSlavoGermanic, primary, alternate);
                    break;

                case 'H':
                    // Keep H only between vowels or at the start before a vowel.
                    if ((current == 0 || IsVowel(word, current - 1)) && IsVowel(word, current + 1))
                    {
                        Add(primary, alternate, "H");
                        current += 2;
                    }
                    else
                    {
                        current += 1;
                    }
                    break;

                case 'J':
                    if (At(word, current, 4) == "JOSE")
                    {
                        // Spanish 'J' (as in "Jose") -> primary keeps J, alternate is H-like.
                        Add(primary, alternate, "J", "H");
                    }
                    else
                    {
                        Add(primary, alternate, "J", current == 0 ? "A" : "J");
                    }
                    current += At(word, current + 1, 1) == "J" ? 2 : 1;
                    break;

                case 'K':
                    Add(primary, alternate, "K");
                    current += At(word, current + 1, 1) == "K" ? 2 : 1;
                    break;

                case 'L':
                    Add(primary, alternate, "L");
                    current += At(word, current + 1, 1) == "L" ? 2 : 1;
                    break;

                case 'M':
                    Add(primary, alternate, "M");
                    current += At(word, current + 1, 1) == "M" ? 2 : 1;
                    break;

                case 'N':
                    Add(primary, alternate, "N");
                    current += At(word, current + 1, 1) == "N" ? 2 : 1;
                    break;

                case 'Ñ':
                    Add(primary, alternate, "N");
                    current += 1;
                    break;

                case 'P':
                    if (At(word, current + 1, 1) == "H")
                    {
                        Add(primary, alternate, "F");
                        current += 2;
                    }
                    else
                    {
                        Add(primary, alternate, "P");
                        current += At(word, current + 1, 1) is "P" or "B" ? 2 : 1;
                    }
                    break;

                case 'Q':
                    Add(primary, alternate, "K");
                    current += At(word, current + 1, 1) == "Q" ? 2 : 1;
                    break;

                case 'R':
                    Add(primary, alternate, "R");
                    current += At(word, current + 1, 1) == "R" ? 2 : 1;
                    break;

                case 'S':
                    current = EncodeS(word, current, primary, alternate);
                    break;

                case 'T':
                    current = EncodeT(word, current, primary, alternate);
                    break;

                case 'V':
                    Add(primary, alternate, "F");
                    current += At(word, current + 1, 1) == "V" ? 2 : 1;
                    break;

                case 'W':
                    if (At(word, current, 2) == "WR")
                    {
                        Add(primary, alternate, "R");
                        current += 2;
                    }
                    else if (current == 0 && (IsVowel(word, current + 1) || At(word, current, 2) == "WH"))
                    {
                        Add(primary, alternate, "A", IsVowel(word, current + 1) ? "F" : "A");
                        current += 1;
                    }
                    else
                    {
                        current += 1;
                    }
                    break;

                case 'X':
                    Add(primary, alternate, "KS");
                    current += At(word, current + 1, 1) is "C" or "X" ? 2 : 1;
                    break;

                case 'Z':
                    Add(primary, alternate, "S");
                    current += At(word, current + 1, 1) == "Z" ? 2 : 1;
                    break;

                default:
                    current += 1;
                    break;
            }
        }

        return (Trim(primary), Trim(alternate));
    }

    private static int EncodeC(string word, int current, StringBuilder p, StringBuilder a)
    {
        if (At(word, current, 2) == "CH")
        {
            // 'CH' -> 'K' (primary) and 'X' (alternate) so "Bacher"/"Baker" can share a reading.
            Add(p, a, "K", "X");
            return current + 2;
        }
        if (At(word, current, 2) == "CC" && !(current == 1 && word[0] == 'M'))
        {
            if (IsOneOf(word, current + 2, 'I', 'E', 'H') && At(word, current + 2, 2) != "HU")
            {
                Add(p, a, "KS");
                return current + 3;
            }
            Add(p, a, "K");
            return current + 2;
        }
        if (At(word, current, 2) is "CK" or "CG" or "CQ")
        {
            Add(p, a, "K");
            return current + 2;
        }
        if (At(word, current, 2) is "CI" or "CE" or "CY")
        {
            Add(p, a, "S");
            return current + 2;
        }
        Add(p, a, "K");
        return current + (At(word, current + 1, 1) is "C" ? 2 : 1);
    }

    private static int EncodeG(string word, int current, bool slavoGermanic, StringBuilder p, StringBuilder a)
    {
        if (At(word, current + 1, 1) == "H")
        {
            if (current > 0 && !IsVowel(word, current - 1))
            {
                Add(p, a, "K");
                return current + 2;
            }
            // silent GH (e.g. "Wright", "Hugh")
            return current + 2;
        }
        if (At(word, current + 1, 1) == "N")
        {
            // 'GN' -> 'N' (silent G), with 'KN' alternate for non-Slavo-Germanic.
            Add(p, a, "N", slavoGermanic ? "N" : "KN");
            return current + 2;
        }
        if (IsOneOf(word, current + 1, 'I', 'E', 'Y'))
        {
            Add(p, a, "J", "K");
            return current + 2;
        }
        Add(p, a, "K");
        return current + (At(word, current + 1, 1) == "G" ? 2 : 1);
    }

    private static int EncodeS(string word, int current, StringBuilder p, StringBuilder a)
    {
        if (At(word, current, 2) == "SH")
        {
            Add(p, a, "X");
            return current + 2;
        }
        if (At(word, current, 3) is "SIO" or "SIA")
        {
            Add(p, a, "S", "X");
            return current + 3;
        }
        // Any 'SC' (including 'SCH', whose sub-case is handled inside).
        if (At(word, current, 2) == "SC")
        {
            if (At(word, current, 3) == "SCH")
            {
                Add(p, a, "X", "SK");
                return current + 3;
            }
            Add(p, a, "SK");
            return current + 2;
        }
        Add(p, a, "S");
        return current + (At(word, current + 1, 1) is "S" or "Z" ? 2 : 1);
    }

    private static int EncodeT(string word, int current, StringBuilder p, StringBuilder a)
    {
        if (At(word, current, 2) == "TH")
        {
            // 'TH' -> '0' (theta) primary, 'T' alternate, so "Smith"/"Smyth" share primary.
            Add(p, a, "0", "T");
            return current + 2;
        }
        if (At(word, current, 3) is "TIO" or "TIA")
        {
            Add(p, a, "X");
            return current + 3;
        }
        Add(p, a, "T");
        return current + (At(word, current + 1, 1) is "T" or "D" ? 2 : 1);
    }

    private static string At(string word, int start, int count)
    {
        if (start < 0 || start >= word.Length)
            return "";
        count = Math.Min(count, word.Length - start);
        return word.Substring(start, count);
    }

    private static bool IsOneOf(string word, int index, params char[] chars)
        => index >= 0 && index < word.Length && Array.IndexOf(chars, word[index]) >= 0;

    private static bool IsVowel(string word, int index)
        => index >= 0 && index < word.Length && "AEIOUY".IndexOf(word[index]) >= 0;

    private static void Add(StringBuilder primary, StringBuilder alternate, string value)
        => Add(primary, alternate, value, value);

    private static void Add(StringBuilder primary, StringBuilder alternate, string primaryValue, string alternateValue)
    {
        if (primary.Length < MaxLength)
            primary.Append(primaryValue);
        if (alternate.Length < MaxLength)
            alternate.Append(alternateValue);
    }

    private static string Trim(StringBuilder sb)
        => sb.Length <= MaxLength ? sb.ToString() : sb.ToString(0, MaxLength);
}
