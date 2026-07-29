using System.Text.RegularExpressions;

namespace MageRide.Ride.Domain;

/// <summary>
/// The one shape a phone number may have on this service's surface —
/// <c>_shared.yaml#PhoneE164</c>'s <c>^\+947\d{8}$</c>.
/// </summary>
/// <remarks>
/// <para>
/// Normalising before validating is what makes a booker's <c>077 123 4567</c> and a rider's
/// <c>+94771234567</c> the same subject: <c>rider_phone_hash</c> is a digest, so two spellings of
/// one number would hash differently and the P-12 audit would show two subjects where there is one.
/// </para>
/// <para>
/// Only Sri Lankan mobiles are accepted, because that is the pattern D3' publishes and because
/// every downstream use — the FCM lookup, the SMS, the driver's <c>tel:</c> — is a Sri Lankan
/// mobile. A landline or a foreign number is <c>400 invalid-phone</c> rather than a row nothing can
/// reach.
/// </para>
/// </remarks>
public static partial class RiderPhone
{
    /// <summary>Thirteen digits plus generous separators. Beyond this nothing is a typo.</summary>
    private const int MaxInputLength = 32;

    /// <summary>
    /// Accepts <c>+94771234567</c>, <c>0094771234567</c>, <c>0771234567</c> and <c>771234567</c>,
    /// with spaces, hyphens and brackets anywhere, and yields the canonical E.164 form.
    /// </summary>
    public static bool TryNormalise(string? value, out string normalised)
    {
        normalised = string.Empty;

        // The longest spelling accepted below is thirteen digits plus separators; anything longer
        // is not a mistyped number, and the bound is what keeps the stack buffer a constant.
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxInputLength)
        {
            return false;
        }

        Span<char> buffer = stackalloc char[MaxInputLength];
        var length = 0;

        foreach (var character in value)
        {
            if (char.IsAsciiDigit(character))
            {
                buffer[length++] = character;
            }
        }

        var digits = new string(buffer[..length]);

        // A leading '+' is dropped by the digit filter, so the four accepted spellings collapse to
        // their national significant number and are re-prefixed once.
        var national = digits switch
        {
            { Length: 11 } when digits.StartsWith("94", StringComparison.Ordinal) => digits[2..],
            { Length: 13 } when digits.StartsWith("0094", StringComparison.Ordinal) => digits[4..],
            { Length: 10 } when digits[0] == '0' => digits[1..],
            { Length: 9 } => digits,
            _ => null,
        };

        if (national is null)
        {
            return false;
        }

        var candidate = "+94" + national;

        if (!E164().IsMatch(candidate))
        {
            return false;
        }

        normalised = candidate;
        return true;
    }

    [GeneratedRegex(@"^\+947\d{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex E164();
}
