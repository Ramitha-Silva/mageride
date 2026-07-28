using System.Text.RegularExpressions;

namespace MageRide.Iam.Domain;

/// <summary>
/// The one shape a MageRide phone number takes: Sri Lankan mobile in E.164,
/// <c>^\+947\d{8}$</c> (D3' iam-svc otp/request, <c>_shared.yaml#/schemas/PhoneE164</c>).
/// </summary>
public static partial class PhoneNumbers
{
    /// <summary>
    /// Normalises the common local spellings onto E.164, or fails.
    /// </summary>
    /// <remarks>
    /// Sri Lankan users type <c>0771234567</c> and <c>+94 77 123 4567</c> at least as often as
    /// the canonical form, and a rejected sign-in is the first thing a new user sees. Separators
    /// and a leading national <c>0</c> are normalised; anything else is a
    /// <c>400 invalid-phone</c>, because a number we guessed at is a number the OTP never
    /// reaches.
    /// </remarks>
    public static bool TryNormalise(string? value, out string normalised)
    {
        normalised = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        Span<char> digits = stackalloc char[16];
        var length = 0;
        var leadingPlus = false;

        foreach (var c in value)
        {
            if (c is ' ' or '-' or '(' or ')' or '.')
            {
                continue;
            }

            if (c == '+' && length == 0 && !leadingPlus)
            {
                leadingPlus = true;
                continue;
            }

            if (!char.IsAsciiDigit(c) || length == digits.Length)
            {
                return false;
            }

            digits[length++] = c;
        }

        var candidate = new string(digits[..length]);

        // 0771234567 -> +94771234567. Only when there was no explicit country code.
        if (!leadingPlus && candidate.StartsWith('0'))
        {
            candidate = "94" + candidate[1..];
        }

        candidate = "+" + candidate;

        if (!SriLankanMobile().IsMatch(candidate))
        {
            return false;
        }

        normalised = candidate;
        return true;
    }

    [GeneratedRegex(@"^\+947\d{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex SriLankanMobile();
}
