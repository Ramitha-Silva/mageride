using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace MageRide.Fleet.Domain;

/// <summary>
/// Canonicalises a vehicle registration number so D-37's uniqueness rule actually holds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Character for character, registry-svc's <c>RegistrationNumbers</c>.</b> D-37 makes a
/// registration unique across the live set and <c>ux_vehicles_regno_active</c> enforces it as a
/// unique index over the <em>stored text</em> — so the rule holds only while every writer stores
/// the same text for the same plate. There are two writers: a driver registering their own Mode C
/// vehicle in the Driver App, and an operator onboarding a Mode A/B vehicle here. If the two
/// normalise differently, <c>WP QA-1234</c> from the Fleet Portal and <c>WP-QA-1234</c> from the
/// Driver App become two rows for one plate and a passenger reading a number off the side of a bus
/// no longer identifies one registration.
/// </para>
/// <para>
/// <b>So this is a copy, and the copy is the problem.</b> Unlike the vocabulary duplications
/// elsewhere in this build — ocr-svc's <c>DocumentKinds</c>, this service's own
/// <see cref="VehicleDocumentStatuses"/> — the two sides here are not agreeing on a *value* that a
/// test can compare; they are agreeing on an *algorithm*, and two implementations of an algorithm
/// drift in ways a set-equality assertion cannot see. <c>FleetRegistrationNumberTests</c> pins the
/// canonical form against a table of inputs so a divergence fails a build rather than a plate
/// lookup. <b>It belongs in <c>MageRide.Shared.Primitives</c>; raised in the C059 handoff</b> — not
/// moved here, because relocating registry-svc's type is a change to a component this one is not
/// building.
/// </para>
/// <para>
/// The canonical form is upper case with every run of spaces, hyphens and underscores collapsed to
/// a single hyphen. Anything outside <c>[A-Za-z0-9]</c> and those separators is refused rather than
/// stripped: silently deleting a character would let two different plates canonicalise to one.
/// </para>
/// </remarks>
public static class FleetRegistrationNumbers
{
    /// <summary><c>fleet.yaml</c> caps <c>registrationNumber</c> at 32, as <c>registry.yaml</c> does.</summary>
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
                // Held back rather than appended on sight, so a leading or trailing separator never
                // survives and "WP - QA" does not become "WP--QA".
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
