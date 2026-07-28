using System.Text.Json;
using MageRide.Shared.Http;
using MageRide.Shared.Messaging;
using MageRide.TripState.Domain;

namespace MageRide.TripState.Sessions;

/// <summary>The event names trip-state-svc publishes on <c>trip.events</c> (D6' §2.1).</summary>
public static class SessionEventTypes
{
    /// <summary>A vehicle is now broadcasting for a journey. fanout-svc puts it on the live map.</summary>
    public const string SessionStarted = "session.started";

    /// <summary>The journey is over. <c>endReason</c> says how, and whether it can be restarted.</summary>
    public const string SessionEnded = "session.ended";

    /// <summary>An auto-ended session was resumed inside the grace window (US-5.10).</summary>
    public const string SessionRestarted = "session.restarted";
}

/// <summary>
/// The envelopes trip-state-svc writes into <c>trips.outbox</c> (D6' §2.4, migration 0505).
/// </summary>
/// <remarks>
/// <para>
/// <b>The topic is spec'd and the envelopes are not.</b> D6' §2.1 has <c>trip.events</c> —
/// "Mode A/B session transitions from trip-state-svc, key vehicleId" — so unlike C028's
/// <c>registry.events</c> and C030's <c>provisioning.events</c> nothing new is claimed here. But
/// D6' §2.2 prints no schema for any of these three, so the shapes below are this service's and
/// are raised as a micro-change-set in the C031 handoff.
/// </para>
/// <para>
/// <b>The aggregate id is the vehicle</b>, matching the topic's partition key. Keying by session
/// would order events per session, and the ordering that matters is per vehicle: an end and the
/// start that follows it must arrive in that order, or fanout-svc removes the vehicle from the
/// live map immediately after adding it back.
/// </para>
/// <para>
/// Every payload carries <c>driverId</c> as well as <c>vehicleId</c>. D6' §5.2 scopes the live map
/// by vehicle, but the E-01 push US-5.9 asks for ("you were auto-ended, here is why") is addressed
/// to a person, and a consumer that had only the vehicle would have to ask this service on the hot
/// path who was driving it.
/// </para>
/// </remarks>
public static class SessionEvents
{
    public static OutboxRecord SessionStarted(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return Record(
            SessionEventTypes.SessionStarted,
            session.VehicleId,
            new
            {
                sessionId = session.Id,
                vehicleId = session.VehicleId,
                driverId = session.DriverId,
                mode = session.Mode,
                routeId = session.RouteId,
                // AL-32: a consumer showing "journey started" needs to know whether a human
                // pressed the button or the ignition did, because the dashboard renders the same
                // state either way but a support timeline does not.
                startedBy = session.StartedBy,
                startedAt = session.StartedAt,
            });
    }

    /// <param name="restartableUntil">When the US-5.10 grace closes, or null for a driver's own
    /// End Journey. Carried so the push US-5.9 sends can say how long they have.</param>
    public static OutboxRecord SessionEnded(Session session, DateTimeOffset? restartableUntil)
    {
        ArgumentNullException.ThrowIfNull(session);

        return Record(
            SessionEventTypes.SessionEnded,
            session.VehicleId,
            new
            {
                sessionId = session.Id,
                vehicleId = session.VehicleId,
                driverId = session.DriverId,
                mode = session.Mode,
                endReason = session.EndReason,
                endedBy = session.EndedBy,
                endedAt = session.EndedAt,
                restartableUntil,
            });
    }

    public static OutboxRecord SessionRestarted(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return Record(
            SessionEventTypes.SessionRestarted,
            session.VehicleId,
            new
            {
                sessionId = session.Id,
                vehicleId = session.VehicleId,
                driverId = session.DriverId,
                mode = session.Mode,
                restartedAt = session.StartedAt,
            });
    }

    private static OutboxRecord Record(string eventType, Guid vehicleId, object payload) =>
        new(vehicleId, eventType, JsonSerializer.Serialize(payload, MageRideJson.StorageOptions));
}
