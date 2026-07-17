using PhoneNumbers;

namespace Linkuity.Core.Normalization;

internal static class PhoneNormalizer
{
    private static readonly PhoneNumberUtil PhoneUtil = PhoneNumberUtil.GetInstance();

    internal static string? Normalize(string value)
    {
        try
        {
            var number = PhoneUtil.Parse(value, null);
            if (PhoneUtil.IsValidNumber(number))
                return PhoneUtil.Format(number, PhoneNumberFormat.E164);
        }
        catch { }

        try
        {
            var number = PhoneUtil.Parse(value, "US");
            if (PhoneUtil.IsValidNumber(number))
                return PhoneUtil.Format(number, PhoneNumberFormat.E164);
        }
        catch { }

        return null;
    }
}
