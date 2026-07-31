using Dapper;
using MageRide.Shared.Persistence;
using MageRide.Voip.Domain;

namespace MageRide.Voip.Persistence;

/// <summary>An open or closed <c>comms.voip_sessions</c> row.</summary>
public sealed record VoipSession(Guid Id, Guid RideId, string LivekitRoom, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);

/// <summary>
/// The <c>rides.rides</c> read this service makes, and the two <c>comms</c> tables it writes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The ride is read directly, not fetched from ride-svc.</b> The platform's established shape for
/// a cross-context <em>read</em> (safety-svc reads <c>rides.rides</c> and <c>iam.users</c>,
/// support-svc reads <c>content.faq_articles</c>), and the reason here is availability: an in-app
/// call is what somebody reaches for when a driver cannot find them, and a hop through ride-svc
/// would make a ride-svc outage into a calling outage on top of it. CLAUDE.md's outbox rule governs
/// cross-service <em>state changes</em>; nothing read here is changed here.
/// </para>
/// <para>
/// <b>Four columns and no more.</b> The projection is exactly what P-05 needs to decide who may
/// call whom. It deliberately does not select <c>rider_phone_hash</c>, <c>recipient_phone</c> or
/// anything joining <c>iam.users</c> — AL-48 puts the counterparty's number on ride-svc's ride
/// detail, and a number this service cannot read is a number it cannot leak.
/// </para>
/// </remarks>
public interface IVoipRepository
{
    /// <summary>The parties to a ride, or null when there is no such ride.</summary>
    Task<RideParticipants?> FindRideAsync(Guid rideId, CancellationToken cancellationToken);

    /// <summary>
    /// The open session for a room, creating it if there is none.
    /// </summary>
    /// <remarks>
    /// One room per ride means one open session per ride, and
    /// <c>ux_voip_sessions_open_room</c> (migration 1311) is what makes that true rather than
    /// merely intended: the driver and the rider both call <c>/v1/calls/start</c> for the same
    /// conversation, and without it each would open a session that the other's teardown would not
    /// close.
    /// </remarks>
    Task<VoipSession> OpenSessionAsync(Guid rideId, string roomName, CancellationToken cancellationToken);

    /// <summary>Closes every open session for a ride and returns the rooms that were closed.</summary>
    Task<IReadOnlyList<string>> EndSessionsAsync(Guid rideId, CancellationToken cancellationToken);

    /// <summary>Records a call attempt. Returns the row id.</summary>
    Task<Guid> LogCallAsync(
        Guid rideId, Guid callerId, string calleeRole, string callType, CancellationToken cancellationToken);

    /// <summary>
    /// Records how a call ended. False when the row is not this caller's, or is already closed.
    /// </summary>
    /// <remarks>
    /// Scoped to the caller, and guarded on <c>ended_at IS NULL</c>: the outcome is reported by the
    /// handset that placed the call, and a second report — a retry, a resumed app — must not
    /// overwrite the first with a worse one.
    /// </remarks>
    Task<bool> CloseCallAsync(Guid callId, Guid callerId, string outcome, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IVoipRepository"/>
internal sealed class VoipRepository(INpgsqlConnectionFactory connections) : IVoipRepository
{
    public async Task<RideParticipants?> FindRideAsync(Guid rideId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<RideParticipants>(new CommandDefinition(
            """
            SELECT id            AS RideId,
                   passenger_id  AS PassengerId,
                   booker_id     AS BookerId,
                   rider_id      AS RiderId,
                   is_proxy      AS IsProxy,
                   accepted_driver_id AS AcceptedDriverId,
                   state         AS State
              FROM rides.rides
             WHERE id = @RideId;
            """,
            new { RideId = rideId },
            cancellationToken: cancellationToken));
    }

    public async Task<VoipSession> OpenSessionAsync(
        Guid rideId, string roomName, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        // ON CONFLICT over the partial unique index, so two callers racing on one ride produce one
        // session between them. DO UPDATE rather than DO NOTHING: DO NOTHING returns no row, and
        // the caller needs the id either way.
        return await connection.QuerySingleAsync<VoipSession>(new CommandDefinition(
            """
            INSERT INTO comms.voip_sessions (ride_id, livekit_room)
            VALUES (@RideId, @RoomName)
            ON CONFLICT (livekit_room) WHERE ended_at IS NULL
              DO UPDATE SET livekit_room = EXCLUDED.livekit_room
            RETURNING id, ride_id AS RideId, livekit_room AS LivekitRoom, started_at AS StartedAt, ended_at AS EndedAt;
            """,
            new { RideId = rideId, RoomName = roomName },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<string>> EndSessionsAsync(Guid rideId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rooms = await connection.QueryAsync<string>(new CommandDefinition(
            """
            UPDATE comms.voip_sessions
               SET ended_at = now()
             WHERE ride_id = @RideId AND ended_at IS NULL
            RETURNING livekit_room;
            """,
            new { RideId = rideId },
            cancellationToken: cancellationToken));

        return [.. rooms];
    }

    public async Task<Guid> LogCallAsync(
        Guid rideId, Guid callerId, string calleeRole, string callType, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            """
            INSERT INTO comms.call_log (ride_id, caller_id, callee_role, call_type)
            VALUES (@RideId, @CallerId, @CalleeRole, @CallType)
            RETURNING id;
            """,
            new { RideId = rideId, CallerId = callerId, CalleeRole = calleeRole, CallType = callType },
            cancellationToken: cancellationToken));
    }

    public async Task<bool> CloseCallAsync(
        Guid callId, Guid callerId, string outcome, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE comms.call_log
               SET outcome = @Outcome, ended_at = now()
             WHERE id = @CallId AND caller_id = @CallerId AND ended_at IS NULL;
            """,
            new { CallId = callId, CallerId = callerId, Outcome = outcome },
            cancellationToken: cancellationToken));

        return affected > 0;
    }
}
