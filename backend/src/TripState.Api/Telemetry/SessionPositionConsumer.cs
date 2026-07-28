using Confluent.Kafka;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using MageRide.Shared.Primitives;
using MageRide.Shared.Telemetry;
using MageRide.TripState.Configuration;
using MageRide.TripState.Domain;
using MageRide.TripState.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MageRide.TripState.Telemetry;

/// <summary>
/// Consumes <c>telemetry.normalized</c> and keeps each live session's idle clock and last position
/// (US-5.3, US-5.4).
/// </summary>
/// <remarks>
/// <para>
/// <b>US-5.3 says "no movement detected", and something has to detect it.</b> Neither spec gives
/// the idle timer an input: <c>trips.sessions</c> has no "last moved" column, and deriving one from
/// <c>telemetry.positions</c> would put a hypertable scan on a sweep that runs every minute. This
/// consumer is that input. It is also what makes US-5.4 possible — the arrival fence needs to know
/// where the vehicle is, and this is the only stream that says.
/// </para>
/// <para>
/// <b>Reporting is not moving.</b> A bus parked at a terminus keeps publishing fixes at its
/// standby cadence, and counting those as activity would make the 30-minute timer unreachable —
/// exactly the failure US-5.3 exists to prevent. So a fix advances <c>last_position_*</c> always
/// and <c>last_movement_at</c> only when the vehicle has actually moved: either it reported speed
/// above <see cref="TripStateOptions.MovementSpeedMps"/>, or it is more than
/// <see cref="TripStateOptions.MovementThresholdM"/> from where it was. Two independent signals,
/// because a cheap tracker reports no speed and consumer GNSS wanders tens of metres while parked.
/// </para>
/// <para>
/// <b>Latest, not earliest.</b> A consumer that has been down for ten minutes must not wake up and
/// replay ten minutes of stale fixes into the idle clock: every one of them would be treated as
/// current activity and would keep a session alive that stopped moving a quarter of an hour ago.
/// Same reasoning as position-processor's, for the same reason — this is a current-state read of a
/// history stream. Only affects a group with no committed offset.
/// </para>
/// <para>
/// A fix for a vehicle with no live session is dropped, not an error: Mode C vehicles publish on
/// the same stream all day and have no tracking session by construction (R-01).
/// </para>
/// </remarks>
public sealed class SessionPositionConsumer(
    IServiceProvider services,
    IOptions<KafkaOptions> kafkaOptions,
    IOptions<TripStateOptions> tripOptions,
    ILogger<SessionPositionConsumer> logger) : KafkaTopicConsumer(kafkaOptions, logger)
{
    private readonly TripStateOptions _options =
        tripOptions?.Value ?? throw new ArgumentNullException(nameof(tripOptions));

    /// <summary>Fixes this replica has applied to a live session. Read by the US-5.3 test.</summary>
    public long Applied => Interlocked.Read(ref _applied);

    private long _applied;

    protected override string Topic => EventTopics.TelemetryNormalized;

    protected override string GroupId => _options.PositionConsumerGroup;

    protected override AutoOffsetReset OffsetReset => AutoOffsetReset.Latest;

    protected override async Task HandleAsync(
        ConsumeResult<string, byte[]> message, CancellationToken cancellationToken)
    {
        // The key is the vehicleId position-processor stamped, which it read from the authenticated
        // MQTT topic. An unkeyed record cannot be attributed to a session.
        if (!Guid.TryParse(message.Message.Key, out var vehicleId))
        {
            throw new PoisonMessageException(
                $"A {EventTopics.TelemetryNormalized} record at offset {message.Offset.Value} carries no vehicle key " +
                $"('{message.Message.Key}'). position-processor-svc keys every record by vehicleId.");
        }

        var sample = PositionSampleCodec.TryDecode(message.Message.Value);

        if (sample is null || !sample.IsWellFormed)
        {
            // Dropped rather than retried. An undecodable payload will not decode on the next
            // attempt either, and a session's idle clock is not worth stalling a partition for.
            Logger.LogDebug(
                "Discarded an undecodable {Topic} payload for vehicle {VehicleId} at offset {Offset}",
                EventTopics.TelemetryNormalized, vehicleId, message.Offset.Value);

            return;
        }

        await ApplyAsync(new VehicleFix(vehicleId, sample.Point, sample.SampleTs, sample.SpeedMps), cancellationToken);
    }

    /// <summary>
    /// Applies one fix to the vehicle's live session, if it has one.
    /// </summary>
    /// <remarks>Exposed so a test can drive US-5.3 and US-5.4 without standing up a broker — the
    /// consume loop is the kernel's and is proven by its own tests.</remarks>
    internal async Task<bool> ApplyAsync(VehicleFix fix, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fix);

        await using var scope = services.CreateAsyncScope();

        var connectionFactory = scope.ServiceProvider.GetRequiredService<INpgsqlConnectionFactory>();
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionRepository>();

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var session = await sessions.FindActiveByVehicleAsync(connection, null, fix.VehicleId, cancellationToken);

        if (session is null)
        {
            return false;
        }

        var previous = await sessions.FindLastPositionAsync(connection, null, session.Id, cancellationToken);
        var moved = HasMoved(previous, fix);

        await sessions.RecordMovementAsync(
            connection, null, session.Id, fix.Point, fix.SampleTs, moved, cancellationToken);

        Interlocked.Increment(ref _applied);

        return moved;
    }

    /// <summary>
    /// Whether this fix counts as movement (US-5.3).
    /// </summary>
    /// <remarks>
    /// Speed first because it is the cheaper and stronger signal when a device reports it; the
    /// displacement test is what covers the trackers that do not. A first fix on a session has no
    /// previous position to compare against and is treated as movement — the vehicle has only just
    /// gone live, and starting the idle clock as though it were already parked would be wrong.
    /// </remarks>
    internal bool HasMoved(GeoPoint? previous, VehicleFix fix)
    {
        ArgumentNullException.ThrowIfNull(fix);

        if (fix.SpeedMps is { } speed && speed >= _options.MovementSpeedMps)
        {
            return true;
        }

        return previous is not { } from || DistanceM(from, fix.Point) >= _options.MovementThresholdM;
    }

    /// <summary>
    /// Great-circle metres between two fixes.
    /// </summary>
    /// <remarks>
    /// Computed here rather than by PostGIS. The alternative is an <c>ST_Distance</c> per fix, and
    /// this runs once per position of every live vehicle on the platform — D5' §5.2 puts a Mode A/B
    /// session at a fix every 5–10 s, so a fleet of a thousand buses is a couple of hundred round
    /// trips a second for an answer a spherical formula gives exactly as well. The fence itself
    /// (US-5.4) stays in PostGIS, where it is one query over the armed subset rather than per fix.
    /// </remarks>
    internal static double DistanceM(GeoPoint from, GeoPoint to)
    {
        const double earthRadiusM = 6_371_008.8;

        var lat1 = double.DegreesToRadians(from.Latitude);
        var lat2 = double.DegreesToRadians(to.Latitude);
        var deltaLat = lat2 - lat1;
        var deltaLng = double.DegreesToRadians(to.Longitude - from.Longitude);

        var h = Math.Sin(deltaLat / 2) * Math.Sin(deltaLat / 2)
                + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(deltaLng / 2) * Math.Sin(deltaLng / 2);

        return 2 * earthRadiusM * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }
}
