using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace MageRide.Registry.Domain;

/// <summary>
/// Canonicalises a vehicle registration number so D-37's uniqueness rule actually holds.
/// </summary>
/// <remarks>
/// <para>
/// D-37 makes a registration unique across the live set, and 0303 enforces it with
/// <c>ux_vehicles_regno_active</c> — a unique index over the <em>stored text</em>. No spec says
/// what that text should look like, so without normalisation the rule is bypassed by typing
/// <c>wp qa-1234</c> where <c>WP-QA-1234</c> is already registered: two rows, one plate, and
/// the plate a passenger reads off the vehicle no longer identifies one registration.
/// </para>
/// <para>
/// The canonical form is upper case with every run of spaces, hyphens and underscores
/// collapsed to a single hyphen, which keeps both of the ways Sri Lankan plates are written
/// (<c>WP QA-1234</c>, <c>QA-1234</c>) readable while making them compare equal. Anything
/// outside <c>[A-Za-z0-9]</c> and those separators is refused rather than stripped: silently
/// deleting a character would let two different plates canonicalise to the same value.
/// </para>
/// </remarks>
public static class RegistrationNumbers
{
    /// <summary><c>registry.yaml#/components/schemas/VehicleRegistration</c> caps it at 32.</summary>
    public const int MaxLength = 32;

    /// <summary>
    /// Canonicalises <paramref name="value"/>, or fails when it is empty, too long, or carries a
    /// character a registration number cannot contain.
    /// </summary>
    public static bool TryNormalise(string? value, [NotNullWhen(true)] out string? normalised)
    {
        normalised = null;

        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxLength)
        {
            return false;
        }

        var builder = new StringBuilder(value.Length);
        var pendingSeparator = false;

        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                // Held back rather than appended on sight, so a leading or trailing separator
                // never survives and "WP - QA" does not become "WP--QA".
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append('-');
                }

                pendingSeparator = false;
                builder.Append(char.ToUpperInvariant(character));
                continue;
            }

            if (character is ' ' or '-' or '_' || char.IsWhiteSpace(character))
            {
                pendingSeparator = true;
                continue;
            }

            return false;
        }

        if (builder.Length == 0)
        {
            return false;
        }

        normalised = builder.ToString();
        return true;
    }
}
