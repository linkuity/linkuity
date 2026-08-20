using PhoneNumbers;

namespace Linkuity.Core.Normalization;

internal static class PhoneNormalizer
{
    private static readonly PhoneNumberUtil PhoneUtil = PhoneNumberUtil.GetInstance();

    /// <summary>
    /// Formats a phone number canonically, or returns null when it cannot be parsed as a valid
    /// number.
    ///
    /// Two passes, in order. The first accepts only numbers carrying their own country code,
    /// which are unambiguous. The second interprets a bare national number as belonging to
    /// <paramref name="defaultRegion"/> — necessarily a guess, since the same digits are a valid
    /// subscriber number in many countries. Returning null rather than guessing harder is
    /// deliberate: the caller keeps the raw value, which is wrong-looking but honest, instead of
    /// a confidently mislabelled number.
    /// </summary>
    internal static string? Normalize(string value, string defaultRegion)
    {
        try
        {
            var number = PhoneUtil.Parse(value, null);
            if (PhoneUtil.IsValidNumber(number))
                return Canonical(number);
        }
        catch { }

        if (string.IsNullOrWhiteSpace(defaultRegion))
            return null;

        try
        {
            var number = PhoneUtil.Parse(value, defaultRegion);
            if (PhoneUtil.IsValidNumber(number))
                return Canonical(number);
        }
        catch { }

        return null;
    }

    /// <summary>
    /// E.164, plus RFC 3966's <c>;ext=</c> parameter when the number carries an extension.
    ///
    /// E.164 has nowhere to put an extension, so formatting to it alone discarded one silently:
    /// two people sharing a switchboard and distinguished only by their extensions produced the
    /// same value, and since the built-in profiles declare phone an identifier, an exact
    /// agreement there floored the pair and merged them.
    ///
    /// Two alternatives were rejected. Refusing to normalize a number that carries an extension
    /// would keep them apart, but "ext 42" and "x42" would then no longer match each other — a
    /// wrong merge traded for a missed one. Formatting everything as RFC 3966 would preserve the
    /// extension too, but it rewrites every number that has none into a different shape
    /// (<c>tel:+1-800-555-0100</c>), churning every stored value, blocking key and pinned sample
    /// expectation for a case that is not broken. Appending only when there is something to
    /// append leaves the extensionless path byte-for-byte as it was.
    /// </summary>
    private static string Canonical(PhoneNumber number)
    {
        var e164 = PhoneUtil.Format(number, PhoneNumberFormat.E164);
        return number.HasExtension && number.Extension.Length > 0
            ? $"{e164};ext={number.Extension}"
            : e164;
    }
}
