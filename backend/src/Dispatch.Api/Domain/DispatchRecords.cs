using MageRide.Shared.Primitives;

namespace MageRide.Dispatch.Domain;

/// <summary>The four values <c>dispatch.driver_presence.state</c>'s CHECK allows (migration 0701).</summary>
public static class PresenceStates
{
    public const string Offline = "OFFLINE";
    public const string Available = "AVAILABLE";
    public const string Offered = "OFFERED";
    public const string OnRide = "ON_RIDE";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Offline, Available, Offered, OnRide,
    };
}

/// <summary>The four values <c>dispatch.offers.status</c>'s CHECK allows (migration 0702).</summary>
public static class OfferStatuses
{
    public const string Offered = "OFFERED";
    public const string Accepted = "ACCEPTED";
    public const string Declined = "DECLINED";
    public const string Expired = "EXPIRED";

    /// <summary>
    /// The two the <c>ux_offers_driver_live</c> partial unique index covers — R-10's "one live
    /// offer per driver". Kept as a set so the repository and the tests cannot drift apart.
    /// </summary>
    public static readonly IReadOnlySet<string> Live = new HashSet<string>(StringComparer.Ordinal)
    {
        Offered, Accepted,
    };
}

/// <summary>A row of <c>dispatch.driver_presence</c> (migration 0701).</summary>
public sealed record PresenceRow(
    Guid DriverId,
    Guid VehicleId,
    string VehicleType,
    string State,
    GeoPoint? Geo,
    GeoPoint? DriverHome,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// A driver who survived both filters: the H3 pre-filter put them in the raw set, and PostGIS
/// <c>ST_DWithin</c> confirmed they are actually within the search radius.
/// </summary>
/// <param name="DistanceM">Exact great-circle metres from the pickup, from <c>ST_Distance</c>.</param>
/// <param name="Geo">
/// Where the post-filter found them. Carried so a failed offer can put the driver back into the
/// GEO index at the position it took them out of, without a second read.
/// </param>
public sealed record Candidate(Guid DriverId, Guid VehicleId, string VehicleType, double DistanceM, GeoPoint Geo);

/// <summary>A row of <c>dispatch.offers</c> (migration 0702).</summary>
public sealed record OfferRow(
    Guid Id,
    Guid RideId,
    Guid DriverId,
    string Status,
    DateTimeOffset SentAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RespondedAt);

/// <summary>An <c>offer_expiry</c> row of <c>rides.timers</c> that is due (migration 0605, R-04).</summary>
public sealed record DueOfferTimer(Guid Id, Guid RideId, Guid OfferId, Guid DriverId, DateTimeOffset FireAt);

/// <summary>
/// The eight Driver-App vehicle tiers (AL-09), which are also the only tiers the candidate index
/// is keyed by.
/// </summary>
/// <remarks>
/// A third copy of the same list (ride-svc's <c>RideVehicleTypes</c> and registry-svc's
/// <c>VehicleTypes.DriverApp</c> are the other two) because the index key
/// <c>geo:drivers:available:{vehicleType}:{cell}</c> embeds the tier verbatim: a value the writer
/// and the reader spell differently does not fail, it silently produces an empty candidate set.
/// <c>bus</c> and <c>train</c> are canonical but Mode A, and are refused by the <c>mode = 'C'</c>
/// gate on the vehicle rather than by this list.
/// </remarks>
public static class DispatchVehicleTypes
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "motorbike", "three_wheeler", "flex", "sedan", "mini_van", "van", "truck", "mini_truck",
    };

    public static bool IsKnown(string? vehicleType) => vehicleType is not null && All.Contains(vehicleType);
}
