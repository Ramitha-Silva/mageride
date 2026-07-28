using System.Collections.Frozen;

namespace MageRide.Fare.Estimates;

/// <summary>
/// The D5' §1.1 tariff table, hard-coded.
/// </summary>
/// <remarks>
/// <para>
/// <b>STUB (C049).</b> The real rows live in <c>fares.tariffs</c> (C005 migration 1001) and are
/// admin-editable through <c>PUT /v1/admin/fares/tariffs</c> (US-14.4), with an
/// <c>effective_from</c> so a rate change never re-prices a ride that was already quoted. This
/// copy exists so the walking skeleton can price a ride before any of that is wired, and it is
/// the first thing C049 deletes.
/// </para>
/// <para>
/// The six passenger rows are D5' §1.1 verbatim. <c>truck</c> and <c>mini_truck</c> are
/// **placeholders**: D5' §1.1 says delivery rates are "admin-configured (Epic 20)" and prints no
/// numbers, and the contract still lets a caller ask for them (<c>RideVehicleType</c>, AL-09), so
/// refusing would break the contract and inventing is the lesser evil. Recorded in the C022
/// handoff — no spec has been read as authorising these two values.
/// </para>
/// </remarks>
public sealed record FareTariff(string VehicleType, long FirstKmMinor, long PerKmMinor)
{
    /// <summary>Rs → minor units (CLAUDE.md "money as minor units").</summary>
    private const long Rupee = 100;

    private static readonly FrozenDictionary<string, FareTariff> ByVehicleType = new[]
    {
        // D5' §1.1, verbatim.
        new FareTariff("motorbike", 80 * Rupee, 60 * Rupee),
        new FareTariff("three_wheeler", 100 * Rupee, 80 * Rupee),
        new FareTariff("flex", 130 * Rupee, 90 * Rupee),
        new FareTariff("sedan", 150 * Rupee, 100 * Rupee),
        new FareTariff("mini_van", 150 * Rupee, 110 * Rupee),
        new FareTariff("van", 150 * Rupee, 120 * Rupee),

        // PLACEHOLDER — see the remarks above. Epic 20 sets the real delivery rates.
        new FareTariff("mini_truck", 200 * Rupee, 130 * Rupee),
        new FareTariff("truck", 250 * Rupee, 150 * Rupee),
    }.ToFrozenDictionary(static t => t.VehicleType, StringComparer.Ordinal);

    /// <summary>
    /// The bookable Mode C set (AL-09, <c>_shared.yaml#RideVehicleType</c>): the six passenger
    /// tiers plus the two delivery types. <c>bus</c> and <c>train</c> are Mode A and have no fare.
    /// </summary>
    public static IReadOnlyCollection<string> BookableVehicleTypes => ByVehicleType.Keys;

    public static bool TryGet(string? vehicleType, out FareTariff tariff)
    {
        if (vehicleType is not null && ByVehicleType.TryGetValue(vehicleType, out var found))
        {
            tariff = found;
            return true;
        }

        tariff = null!;
        return false;
    }
}
