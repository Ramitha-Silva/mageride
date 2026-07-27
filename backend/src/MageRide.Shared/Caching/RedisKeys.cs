using System.Globalization;

namespace MageRide.Shared.Caching;

/// <summary>
/// The Redis key space (ADD §9.4). Every key pattern in one place so two services cannot disagree
/// about where a value lives — <c>position-processor-svc</c> writes
/// <c>driver:availability:{driverId}</c> and <c>dispatch-svc</c> reads it, and a typo in either
/// would silently produce an empty candidate set.
/// </summary>
public static class RedisKeys
{
    /// <summary>GEO set of every active vehicle's last position.</summary>
    public const string GeoLive = "geo:live";

    /// <summary>Postgres notification channel for the outbox dispatcher (E-09).</summary>
    public const string OutboxNotifyChannel = "ride_outbox";

    /// <summary>HASH of cached vehicle metadata (type, colour, route).</summary>
    public static string VehicleMeta(Guid vehicleId) => $"veh:meta:{vehicleId}";

    /// <summary>STREAM of per-cell position events for fan-out consumers.</summary>
    public static string Cell(string h3Index) => $"cell:{h3Index}";

    /// <summary>HASH of live trip state (start time, seats, mode).</summary>
    public static string ActiveTrip(Guid vehicleId) => $"trip:active:{vehicleId}";

    /// <summary>IMEI → vehicleId lookup cache (T-03).</summary>
    public static string Imei(string imei) => $"imei:{imei}";

    /// <summary>Publish-rate token bucket for a vehicle (D-17, E-08).</summary>
    public static string VehicleRateLimit(Guid vehicleId) => $"rate:{vehicleId}";

    /// <summary>Generic token-bucket key: <c>rate:{policy}:{subject}</c>.</summary>
    public static string RateLimit(string policy, string subject) => $"rate:{policy}:{subject}";

    /// <summary>GEO index of dispatch candidates for a vehicle type in an H3 res-5 cell (R-08).</summary>
    public static string AvailableDrivers(string vehicleType, string h3Res5Cell) =>
        $"geo:drivers:available:{vehicleType}:{h3Res5Cell}";

    /// <summary>HASH of <c>{state, lastSeen, vehicleType, level, walletOk, currentRideId?}</c>, TTL 60 s (R-08).</summary>
    public static string DriverAvailability(Guid driverId) => $"driver:availability:{driverId}";

    /// <summary>HASH of the Directional Travel filter, TTL = remaining duration (DT-01).</summary>
    public static string DriverDirectional(Guid driverId) => $"driver:directional:{driverId}";

    /// <summary>Per-Colombo-day activation counter for Directional Travel, TTL 36 h (DT-03).</summary>
    public static string DriverDirectionalUses(Guid driverId, DateOnly businessDate) =>
        $"driver:directional:uses:{driverId}:{businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    /// <summary>HASH of <c>{driverId, expiresAt, status}</c>, TTL 15 s. A fast hint, not authoritative (R-04).</summary>
    public static string Offer(Guid rideId) => $"offer:{rideId}";

    /// <summary>Atomic driver reservation, <c>SET NX PX</c> via Lua (R-10).</summary>
    public static string DriverOfferLock(Guid driverId) => $"lock:driver-offer:{driverId}";

    /// <summary>Single-writer lock held for the duration of a ride state transition.</summary>
    public static string RideLock(Guid rideId) => $"lock:ride:{rideId}";

    /// <summary>Opaque refresh token mirror for O(1) revocation (D-29).</summary>
    public static string RefreshToken(string jti) => $"refresh:{jti}";

    /// <summary>Pre-signed URL to a generated PDPA export ZIP, TTL 30 d (E-06).</summary>
    public static string PdpaExport(Guid requestId) => $"pdpa:export:{requestId}";
}
