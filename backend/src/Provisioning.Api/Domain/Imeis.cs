using System.Diagnostics.CodeAnalysis;
using MageRide.Shared.Errors;

namespace MageRide.Provisioning.Domain;

/// <summary>
/// The IMEI, validated exactly as <c>provisioning.yaml</c>'s <c>Imei</c> schema declares it:
/// <c>^\d{15}$</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The Luhn check digit is deliberately not enforced.</b> An IMEI's fifteenth digit is a Luhn
/// checksum over the first fourteen, so validating it would catch most single-digit typos in a
/// 5,000-row CSV — which is the argument for doing it. It is not done because the contract's
/// pattern is the contract, and because D6' §4.1's device list is full of grey-import GT06 and
/// JT/T 808 units whose firmware reports an IMEI that does not satisfy Luhn. Rejecting one here
/// would leave a tracker that works perfectly over the wire unable to be provisioned at all, and
/// the operator with no way to override it.
/// </para>
/// <para>
/// The value is kept as the caller sent it, digits only. There is no canonicalisation to do — an
/// IMEI has no separators, no case and no leading-zero ambiguity — so unlike a registration
/// number, what is stored is what was typed.
/// </para>
/// </remarks>
public static class Imeis
{
    /// <summary>Digits in an IMEI.</summary>
    public const int Length = 15;

    /// <summary>Whether <paramref name="value"/> is fifteen digits and nothing else.</summary>
    public static bool IsValid([NotNullWhen(true)] string? value)
    {
        if (value is null || value.Length != Length)
        {
            return false;
        }

        foreach (var character in value)
        {
            // Not char.IsDigit: that admits every Unicode decimal digit, so the Sinhala and Tamil
            // digit blocks would pass here and then fail the contract's ASCII-only \d at the
            // gateway, or worse, be stored as a second spelling of an IMEI already bound.
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns the IMEI, or throws <c>400 validation-failed</c> naming the field.</summary>
    public static string Require(string? value, string field = "imei") =>
        IsValid(value)
            ? value
            : throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                [field] = ["imei must be exactly 15 digits."],
            });

    /// <summary>
    /// Parses a path segment. A malformed IMEI is <c>404 not-found</c> rather than a 400.
    /// </summary>
    /// <remarks>
    /// The same reasoning <c>VehicleEndpoints.RequireVehicleId</c> uses: on a path segment "not a
    /// well-formed identifier" and "no such tracker" are the same answer to a caller, and an
    /// endpoint that told them apart would confirm which of two IMEIs is real to somebody
    /// enumerating them. In a request <i>body</i> the field is validated and reported as a 400,
    /// because there the caller is being helped rather than probed.
    /// </remarks>
    public static string RequirePath(string? value) =>
        IsValid(value)
            ? value
            : throw new MageRideException(MageRideErrors.NotFound, "No tracker with that IMEI.");
}
