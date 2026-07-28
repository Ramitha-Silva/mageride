using System.Text.Json;
using Dapper;
using MageRide.Dispatch.Domain;
using MageRide.Shared.Http;
using Npgsql;

namespace MageRide.Dispatch.Persistence;

/// <summary>
/// The R-04 durable backstop: <c>rides.timers</c> rows of kind <c>offer_expiry</c> (migration 0605).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why dispatch-svc writes into the <c>rides</c> schema.</b> The table is ride-svc's by name,
/// but the job is dispatch's by every spec that names an owner: ADD §6 gives <c>dispatch-svc</c>
/// "Quartz.NET (scheduled rides <b>+ offer backstop</b>)", D5' §3.5 puts the durable backstop under
/// *Offer TTL &amp; cascade*, and this component's deliverable list says "a <c>rides.timers</c>
/// backstop row so expiry survives a Redis flush". The write is a single INSERT of one row kind;
/// nothing here touches <c>rides.rides</c>, which stays ride-svc's alone. Recorded as a gap in the
/// C023 handoff — if the schemas are ever split across databases, the timer has to move to
/// <c>dispatch.timers</c> (migration 0708 already exists for the DT-04 case) and ride-svc has to
/// stop being the only place <c>offer_expiry</c> is spelled.
/// </para>
/// <para>
/// <b>Why a poll and not Quartz.</b> ADD §6/§11.11 names Quartz.NET clustered. Quartz brings its
/// own schema, its own clustering protocol and a second scheduler to operate; what R-04 actually
/// requires is "fires ≤1 s after expiry independent of Redis", which one indexed
/// <c>FOR UPDATE SKIP LOCKED</c> claim per half-second gives with the same multi-replica safety.
/// C034/C037 can promote it when the scheduled-ride and no-show timers arrive and there is a
/// second and third caller to justify the dependency.
/// </para>
/// </remarks>
public interface IOfferTimerRepository
{
    /// <summary>Arms the backstop for an offer.</summary>
    Task<Guid> ArmAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        Guid offerId,
        Guid driverId,
        DateTimeOffset fireAt,
        CancellationToken cancellationToken);

    /// <summary>Moves the fire time once ride-svc has stamped the authoritative deadline.</summary>
    Task RescheduleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid timerId,
        DateTimeOffset fireAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Leases due timers to this worker: one statement that selects them
    /// <c>FOR UPDATE SKIP LOCKED</c> and pushes <c>fire_at</c> out by
    /// <paramref name="lease"/> in the same breath.
    /// </summary>
    /// <remarks>
    /// A lease rather than a held row lock, because acting on a claimed timer means writing to
    /// <c>rides.timers</c> again (mark fired, or reschedule on clock skew) — on a different
    /// connection, which would block forever behind the claim's own lock. A lease rather than an
    /// immediate <c>fired_at</c>, because a worker that dies mid-expiry must not take the ride's
    /// only backstop with it: the row simply becomes due again when the lease runs out.
    /// </remarks>
    Task<IReadOnlyList<DueOfferTimer>> ClaimDueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken);

    /// <summary>
    /// Leases the unfired timer for one specific offer, whether or not it is due yet. The D-07
    /// keyspace path uses this: the Redis key expires at the deadline while the durable row is
    /// armed a grace period later, so "due" would miss it by exactly the interval the accelerator
    /// exists to save.
    /// </summary>
    Task<DueOfferTimer?> TryClaimForOfferAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid offerId,
        TimeSpan lease,
        CancellationToken cancellationToken);

    /// <summary>Marks a timer as run. Idempotent — a second call changes nothing.</summary>
    Task MarkFiredAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid timerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Retires every unfired <c>offer_expiry</c> timer for an offer, whatever settled it. Used when
    /// the driver answered before the deadline, so the backstop does not fire against an offer that
    /// is already history.
    /// </summary>
    Task CancelForOfferAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid offerId,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IOfferTimerRepository"/>
public sealed class OfferTimerRepository : IOfferTimerRepository
{
    /// <summary>One of the eight values <c>ck_timers_kind</c> allows (migration 0605).</summary>
    public const string OfferExpiryKind = "offer_expiry";

    public async Task<Guid> ArmAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        Guid offerId,
        Guid driverId,
        DateTimeOffset fireAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // The payload carries the offer and driver so a sweep needs no join to know what it is
        // expiring — and, more to the point, so it expires *that* offer rather than whatever the
        // ride's current one turns out to be by the time the timer runs.
        var payload = JsonSerializer.Serialize(
            new OfferTimerPayload(offerId, driverId), MageRideJson.StorageOptions);

        return await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            $"""
             INSERT INTO rides.timers (ride_id, kind, fire_at, payload)
             VALUES (@RideId, '{OfferExpiryKind}', @FireAt, @Payload::jsonb)
             RETURNING id;
             """,
            new { RideId = rideId, FireAt = fireAt, Payload = payload },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task RescheduleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid timerId,
        DateTimeOffset fireAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.ExecuteAsync(new CommandDefinition(
            "UPDATE rides.timers SET fire_at = @FireAt WHERE id = @TimerId AND fired_at IS NULL;",
            new { TimerId = timerId, FireAt = fireAt },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<DueOfferTimer>> ClaimDueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var rows = await connection.QueryAsync<DueOfferTimer>(new CommandDefinition(
            $"""
             UPDATE rides.timers t
                SET fire_at = now() + make_interval(secs => @LeaseSeconds)
              WHERE t.id IN (
                    SELECT id FROM rides.timers
                     WHERE kind = '{OfferExpiryKind}'
                       AND fired_at IS NULL
                       AND fire_at <= now()
                     ORDER BY fire_at
                     LIMIT @BatchSize
                       FOR UPDATE SKIP LOCKED)
             RETURNING t.id AS Id,
                       t.ride_id AS RideId,
                       (t.payload ->> 'offerId')::uuid AS OfferId,
                       (t.payload ->> 'driverId')::uuid AS DriverId,
                       t.fire_at AS FireAt;
             """,
            new { BatchSize = batchSize, LeaseSeconds = lease.TotalSeconds },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public Task<DueOfferTimer?> TryClaimForOfferAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid offerId,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<DueOfferTimer>(new CommandDefinition(
            $"""
             UPDATE rides.timers t
                SET fire_at = now() + make_interval(secs => @LeaseSeconds)
              WHERE t.id = (
                    SELECT id FROM rides.timers
                     WHERE kind = '{OfferExpiryKind}'
                       AND fired_at IS NULL
                       AND payload ->> 'offerId' = @OfferId
                     ORDER BY fire_at
                     LIMIT 1
                       FOR UPDATE SKIP LOCKED)
             RETURNING t.id AS Id,
                       t.ride_id AS RideId,
                       (t.payload ->> 'offerId')::uuid AS OfferId,
                       (t.payload ->> 'driverId')::uuid AS DriverId,
                       t.fire_at AS FireAt;
             """,
            new { OfferId = offerId.ToString(), LeaseSeconds = lease.TotalSeconds },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task MarkFiredAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid timerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.ExecuteAsync(new CommandDefinition(
            "UPDATE rides.timers SET fired_at = now() WHERE id = @TimerId AND fired_at IS NULL;",
            new { TimerId = timerId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task CancelForOfferAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid offerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Marked fired rather than deleted: rides.timers is also the audit of what the backstop
        // was ever asked to watch, and a deleted row cannot be told apart from one never written.
        return connection.ExecuteAsync(new CommandDefinition(
            $"""
             UPDATE rides.timers
                SET fired_at = now()
              WHERE kind = '{OfferExpiryKind}'
                AND fired_at IS NULL
                AND payload ->> 'offerId' = @OfferId;
             """,
            new { OfferId = offerId.ToString() },
            transaction,
            cancellationToken: cancellationToken));
    }
}

/// <summary>The <c>rides.timers.payload</c> of an <c>offer_expiry</c> row.</summary>
internal sealed record OfferTimerPayload(Guid OfferId, Guid DriverId);
