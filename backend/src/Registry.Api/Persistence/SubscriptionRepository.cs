using Dapper;
using MageRide.Registry.Domain;
using Npgsql;

namespace MageRide.Registry.Persistence;

/// <summary>
/// The <c>subscription</c> schema, as far as registry-svc's two roster routes read it
/// (US-4.5, US-4.7, US-NEW.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>These tables belong to subscription-svc (C061-era, Epic 23), not here.</b> The contract puts
/// <c>GET /v1/vehicles/{id}/subscribers</c> and <c>DELETE .../subscribers/{userId}</c> on
/// registry-svc and says outright that the roster "is held in <c>subscription.grants</c>", so the
/// choice is a cross-schema read or a synchronous hop to a service that does not exist yet. Same
/// judgement as ride-svc's <c>DriverSummaryRepository</c> reading <c>registry.vehicles</c>: two
/// statements, no writes the owning service would not recognise, and no HTTP call on a path a
/// driver is waiting on.
/// </para>
/// <para>
/// The one write here — the passenger's own unsubscribe — is the exact transition D5' §5.4
/// describes (<c>status='unsubscribed'</c>, the row stays MUTED until the owner deletes it,
/// US-4.12/US-13.16). The owner's hard delete stays subscription-svc's.
/// </para>
/// </remarks>
public interface ISubscriptionRepository
{
    /// <summary>
    /// The vehicle's roster, newest grant first. Cursor-paged: the contract returns a
    /// <c>CursorPage</c>, and a Mode B bus can have hundreds of subscribers.
    /// </summary>
    Task<IReadOnlyList<Subscriber>> ListSubscribersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        DateTimeOffset? before,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// The passenger's own unsubscribe (US-NEW.1). Returns <see langword="null"/> when they hold
    /// no active grant on the vehicle, which is a <c>404</c> rather than a silent success.
    /// </summary>
    Task<Subscriber?> UnsubscribeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        Guid passengerId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// Raises a Mode B access request (US-4.5). Returns the existing row when one is already open,
    /// so asking twice is idempotent rather than a <c>409</c> the passenger cannot act on.
    /// </summary>
    Task<AccessRequest> RequestAccessAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        Guid passengerId,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="ISubscriptionRepository"/>
public sealed class SubscriptionRepository : ISubscriptionRepository
{
    private const string GrantColumns =
        "id AS grant_id, vehicle_id, passenger_id, status, granted_at, expires_at, unsubscribed_at";

    public async Task<IReadOnlyList<Subscriber>> ListSubscribersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        DateTimeOffset? before,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // deleted_at IS NULL, not status='active': an unsubscribed grant stays MUTED on the
        // owner's roster until they delete it (US-4.12, US-13.16), and hiding it here would make
        // "who left" unanswerable from the screen the requirement is about.
        var rows = await connection.QueryAsync<Subscriber>(new CommandDefinition(
            $"""
             SELECT {GrantColumns}
               FROM subscription.grants
              WHERE vehicle_id = @VehicleId
                AND deleted_at IS NULL
                AND (@Before::timestamptz IS NULL OR granted_at < @Before)
              ORDER BY granted_at DESC, id
              LIMIT @Limit;
             """,
            new { VehicleId = vehicleId, Before = before, Limit = limit },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public Task<Subscriber?> UnsubscribeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        Guid passengerId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // ck_grants_unsubscribed_pair makes the status and the instant one fact, so they move
        // together. deleted_at IS NULL keeps a grant the owner has already removed out of reach.
        return connection.QuerySingleOrDefaultAsync<Subscriber>(new CommandDefinition(
            $"""
             UPDATE subscription.grants
                SET status = 'unsubscribed', unsubscribed_at = @Now
              WHERE vehicle_id = @VehicleId
                AND passenger_id = @PassengerId
                AND status = 'active'
                AND deleted_at IS NULL
             RETURNING {GrantColumns};
             """,
            new { VehicleId = vehicleId, PassengerId = passengerId, Now = now },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<AccessRequest> RequestAccessAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid vehicleId,
        Guid passengerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // ux_access_request_open is partial over status='pending', so DO NOTHING collapses a
        // second ask onto the first. The follow-up SELECT is what makes the retry return the
        // original id instead of nothing — a passenger tapping twice should see one request.
        var inserted = await connection.QuerySingleOrDefaultAsync<AccessRequest>(new CommandDefinition(
            """
            INSERT INTO subscription.access_requests (vehicle_id, passenger_id)
            VALUES (@VehicleId, @PassengerId)
            ON CONFLICT DO NOTHING
            RETURNING id, vehicle_id, passenger_id, status, requested_at;
            """,
            new { VehicleId = vehicleId, PassengerId = passengerId },
            transaction,
            cancellationToken: cancellationToken));

        return inserted ?? await connection.QuerySingleAsync<AccessRequest>(new CommandDefinition(
            """
            SELECT id, vehicle_id, passenger_id, status, requested_at
              FROM subscription.access_requests
             WHERE vehicle_id = @VehicleId AND passenger_id = @PassengerId AND status = 'pending';
            """,
            new { VehicleId = vehicleId, PassengerId = passengerId },
            transaction,
            cancellationToken: cancellationToken));
    }
}

// Δ AL-57 — `IDriverPayoutRepository` / `DriverPayoutRepository` REMOVED with D-11. OnePay has one
// merchant account per merchant, so the per-driver binding they wrote never existed. Where a
// driver's money goes is `registry.driver_payout_profiles` (AL-58) and payout-svc's weekly sweep;
// migration 1010 drops the table this read.
