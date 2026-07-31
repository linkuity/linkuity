using PhoneNumbers;

namespace Linkuity.Core.Normalization;

internal static class PhoneNormalizer
{
    private static readonly PhoneNumberUtil PhoneUtil = PhoneNumberUtil.GetInstance();

    /// <summary>
    /// Formats a phone number as E.164, or returns null when it cannot be parsed as a valid
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
                return PhoneUtil.Format(number, PhoneNumberFormat.E164);
        }
        catch { }

        if (string.IsNullOrWhiteSpace(defaultRegion))
            return null;

        try
        {
            var number = PhoneUtil.Parse(value, defaultRegion);
            if (PhoneUtil.IsValidNumber(number))
                return PhoneUtil.Format(number, PhoneNumberFormat.E164);
        }
        catch { }

        return null;
    }
}
