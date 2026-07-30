using MageRide.Fanout.Configuration;
using MageRide.Shared.Caching;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MageRide.Fanout.Rides;

/// <summary>
/// Who may join <c>ride:{rideId}</c>, and which vehicle that ride is being driven in.
/// </summary>
/// <param name="RideId">The ride.</param>
/// <param name="PassengerId">The booking account — the same person as the rider unless proxy (P-01).</param>
/// <param name="BookerId">Who arranged it. Equal to <paramref name="PassengerId"/> on an ordinary booking.</param>
/// <param name="RiderId">The third party a proxy booking is for, when they have an account (P-03).</param>
/// <param name="DriverId">The accepted (or offered) driver.</param>
/// <param name="VehicleId">The vehicle, which is what the ride's <c>DriverPosition</c> is read from.</param>
/// <param name="State">The last state seen, so a terminal ride can be refused a new subscriber.</param>
public sealed record RideParticipants(
    Guid RideId,
    Guid PassengerId,
    Guid BookerId,
    Guid? RiderId,
    Guid? DriverId,
    Guid? VehicleId,
    string State)
{
    /// <summary>
    /// Whether <paramref name="userId"/> is on this ride.
    /// </summary>
    /// <remarks>
    /// The four are exactly <c>signalr-hub.md</c> §2.1's membership — "the ride's passenger, its
    /// driver, and, for a proxy booking, the booker" — plus the proxy <em>rider</em>, who is the one
    /// person actually in the car and whom that sentence omits (P-01 makes booker and rider two
    /// people; the rider needs the live driver position more than the booker does). Raised in the
    /// C041 handoff.
    /// </remarks>
    public bool Includes(Guid userId) =>
        userId == PassengerId || userId == BookerId || userId == RiderId || userId == DriverId;
}

/// <summary>
/// fanout-svc's read model of a ride, built from <c>ride.events</c>.
/// </summary>
/// <remarks>
/// <para>
/// It exists for one question — "may this caller subscribe to this ride" — and
/// <c>signalr-hub.md</c> §2 makes answering it mandatory: <c>SubscribeRide</c> is "rejected unless
/// the caller is a participant". A version that joined the group without checking would be a
/// working subscription to a stranger's ride, showing their driver's live position, and it would
/// look from the client exactly like the finished feature.
/// </para>
/// <para>
/// <b>The projection is Redis, not memory.</b> A passenger's socket lands on whichever replica the
/// load balancer picked and the event was consumed by whichever replica owns that partition; those
/// are not the same process, and a per-replica cache would refuse a genuine participant whenever
/// they differed.
/// </para>
/// </remarks>
public interface IRideProjection
{
    /// <summary>What is known about a ride, or <see langword="null"/>.</summary>
    Task<RideParticipants?> ReadAsync(Guid rideId, CancellationToken cancellationToken);

    /// <summary>Records the parties and vehicle of a ride as of its latest event.</summary>
    Task WriteAsync(RideParticipants participants, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRideProjection"/>
public sealed class RideProjection(IConnectionMultiplexer redis, IOptions<FanoutOptions> options) : IRideProjection
{
    private const string PassengerField = "passengerId";
    private const string BookerField = "bookerId";
    private const string RiderField = "riderId";
    private const string DriverField = "driverId";
    private const string VehicleField = "vehicleId";
    private const string StateField = "state";

    private readonly FanoutOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<RideParticipants?> ReadAsync(Guid rideId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var values = await redis.GetDatabase().HashGetAsync(
            RedisKeys.RideParticipants(rideId),
            [PassengerField, BookerField, RiderField, DriverField, VehicleField, StateField]);

        // The passenger is the one member every kind of ride has. Without it the hash is not one
        // this service wrote — an expired key reads as all-null — and guessing at a partial one
        // would be guessing about who is allowed to watch somebody's journey.
        if (ParseGuid(values[0]) is not { } passengerId)
        {
            return null;
        }

        return new RideParticipants(
            rideId,
            passengerId,
            ParseGuid(values[1]) ?? passengerId,
            ParseGuid(values[2]),
            ParseGuid(values[3]),
            ParseGuid(values[4]),
            values[5].IsNullOrEmpty ? string.Empty : values[5].ToString());
    }

    public async Task WriteAsync(RideParticipants participants, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(participants);
        cancellationToken.ThrowIfCancellationRequested();

        var key = RedisKeys.RideParticipants(participants.RideId);
        var fields = new List<HashEntry>
        {
            new(PassengerField, participants.PassengerId.ToString()),
            new(BookerField, participants.BookerId.ToString()),
            new(StateField, participants.State),
        };

        // Written only when present, and never cleared. `ride.requested` names no driver and
        // `ride.accepted` does; overwriting the accepted driver with a null from a later event that
        // happens not to carry one — a penalty, a settlement — would take the passenger's own driver
        // position away from them mid-ride.
        AddIfPresent(fields, RiderField, participants.RiderId);
        AddIfPresent(fields, DriverField, participants.DriverId);
        AddIfPresent(fields, VehicleField, participants.VehicleId);

        var db = redis.GetDatabase();

        await db.HashSetAsync(key, [.. fields]);
        await db.KeyExpireAsync(key, _options.RideProjectionTtl);
    }

    private static void AddIfPresent(List<HashEntry> fields, string name, Guid? value)
    {
        if (value is { } id)
        {
            fields.Add(new HashEntry(name, id.ToString()));
        }
    }

    private static Guid? ParseGuid(RedisValue value) =>
        !value.IsNullOrEmpty && Guid.TryParse(value.ToString(), out var parsed) ? parsed : null;
}
