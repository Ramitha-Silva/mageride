using Dapper;
using MageRide.Provisioning.Domain;
using Npgsql;

namespace MageRide.Provisioning.Persistence;

/// <summary>
/// <c>prov.tracker_bindings</c> — the IMEI ↔ vehicle source of truth (T-03, migration 0401).
/// </summary>
/// <remarks>
/// The Redis <c>imei:{imei}</c> entry is a cache in front of this table and nothing else: every
/// read here is authoritative and every read there may be absent. Consumers that need a verdict
/// rather than a hint come through <c>GET /v1/internal/trackers/{imei}/validate</c>, which falls
/// back to these rows on a cache miss.
/// </remarks>
public interface ITrackerBindingRepository
{
    /// <summary>The one ACTIVE binding for an IMEI, or <see langword="null"/>.</summary>
    Task<TrackerBinding?> FindActiveByImeiAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string imei, CancellationToken cancellationToken);

    /// <summary>
    /// The binding an operator means when they name an IMEI: the ACTIVE one if there is one, else
    /// the most recently changed. A decommissioned tracker still answers <c>GET /v1/trackers/{imei}</c>.
    /// </summary>
    Task<TrackerBinding?> FindLatestByImeiAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string imei, CancellationToken cancellationToken);


    Task<TrackerBinding> InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string imei,
        Guid vehicleId,
        Guid? fleetId,
        string credentialSerial,
        string credentialType,
        string state,
        string? stateReason,
        DateTimeOffset rotatesAt,
        string? source,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves a binding to a new state, but only from <paramref name="fromState"/>.
    /// </summary>
    /// <returns>The updated row, or <see langword="null"/> when it was no longer in that state —
    /// which is how a concurrent revoke and quarantine settle on one winner without a lock.</returns>
    Task<TrackerBinding?> TransitionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid bindingId,
        string fromState,
        string toState,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Points an ACTIVE binding at a freshly minted credential (T-02 rotation).</summary>
    Task<TrackerBinding?> UpdateCredentialAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid bindingId,
        string credentialSerial,
        DateTimeOffset rotatesAt,
        CancellationToken cancellationToken);

    /// <summary>US-3.6 — records which publisher is authoritative for this vehicle.</summary>
    Task<TrackerBinding?> UpdateSourceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid bindingId,
        string source,
        CancellationToken cancellationToken);

    /// <summary>
    /// Claims ACTIVE bindings whose credential is inside its renewal window (T-02).
    /// </summary>
    /// <remarks><c>FOR UPDATE SKIP LOCKED</c>, so two replicas sweeping at once rotate disjoint
    /// sets rather than minting two credentials for one device.</remarks>
    Task<IReadOnlyList<TrackerBinding>> ClaimRotationDueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITrackerBindingRepository"/>
public sealed class TrackerBindingRepository : ITrackerBindingRepository
{
    private const string Columns =
        "id, imei, vehicle_id, fleet_id, credential_serial, credential_type, state, rotates_at, source, " +
        "last_seen_at, signal_strength, battery_mv, sat_count, state_changed_at, state_reason, created_at";

    public Task<TrackerBinding?> FindActiveByImeiAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string imei, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Single-row by construction: ux_tracker_imei_active is a unique index over
        // (imei) WHERE state = 'ACTIVE'.
        return connection.QuerySingleOrDefaultAsync<TrackerBinding>(new CommandDefinition(
            $"""
             SELECT {Columns}
               FROM prov.tracker_bindings
              WHERE imei = @Imei AND state = '{BindingStates.Active}';
             """,
            new { Imei = imei },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<TrackerBinding?> FindLatestByImeiAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string imei, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QueryFirstOrDefaultAsync<TrackerBinding>(new CommandDefinition(
            $"""
             SELECT {Columns}
               FROM prov.tracker_bindings
              WHERE imei = @Imei
              ORDER BY CASE state WHEN '{BindingStates.Active}' THEN 0 ELSE 1 END,
                       state_changed_at DESC,
                       created_at DESC
              LIMIT 1;
             """,
            new { Imei = imei },
            transaction,
            cancellationToken: cancellationToken));
    }


    public Task<TrackerBinding> InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string imei,
        Guid vehicleId,
        Guid? fleetId,
        string credentialSerial,
        string credentialType,
        string state,
        string? stateReason,
        DateTimeOffset rotatesAt,
        string? source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleAsync<TrackerBinding>(new CommandDefinition(
            $"""
             INSERT INTO prov.tracker_bindings
                 (imei, vehicle_id, fleet_id, credential_serial, credential_type, state, state_reason,
                  rotates_at, source)
             VALUES (@Imei, @VehicleId, @FleetId, @CredentialSerial, @CredentialType, @State, @StateReason,
                     @RotatesAt, @Source)
             RETURNING {Columns};
             """,
            new
            {
                Imei = imei,
                VehicleId = vehicleId,
                FleetId = fleetId,
                CredentialSerial = credentialSerial,
                CredentialType = credentialType,
                State = state,
                StateReason = stateReason,
                RotatesAt = rotatesAt,
                Source = source,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<TrackerBinding?> TransitionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid bindingId,
        string fromState,
        string toState,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // `state = @FromState` is a predicate in the UPDATE rather than a check beforehand: two
        // requests revoking the same binding both read ACTIVE, and only the one whose UPDATE
        // matched gets a row back and emits the event.
        return connection.QuerySingleOrDefaultAsync<TrackerBinding>(new CommandDefinition(
            $"""
             UPDATE prov.tracker_bindings
                SET state = @ToState, state_reason = @Reason, state_changed_at = @Now
              WHERE id = @BindingId AND state = @FromState
             RETURNING {Columns};
             """,
            new { BindingId = bindingId, FromState = fromState, ToState = toState, Reason = reason, Now = now },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<TrackerBinding?> UpdateCredentialAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid bindingId,
        string credentialSerial,
        DateTimeOffset rotatesAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // ACTIVE only. Rotating a quarantined or revoked binding would hand a working credential
        // to a device the platform has already decided against.
        return connection.QuerySingleOrDefaultAsync<TrackerBinding>(new CommandDefinition(
            $"""
             UPDATE prov.tracker_bindings
                SET credential_serial = @CredentialSerial, rotates_at = @RotatesAt
              WHERE id = @BindingId AND state = '{BindingStates.Active}'
             RETURNING {Columns};
             """,
            new { BindingId = bindingId, CredentialSerial = credentialSerial, RotatesAt = rotatesAt },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<TrackerBinding?> UpdateSourceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid bindingId,
        string source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<TrackerBinding>(new CommandDefinition(
            $"""
             UPDATE prov.tracker_bindings
                SET source = @Source
              WHERE id = @BindingId AND state = '{BindingStates.Active}'
             RETURNING {Columns};
             """,
            new { BindingId = bindingId, Source = source },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<TrackerBinding>> ClaimRotationDueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var rows = await connection.QueryAsync<TrackerBinding>(new CommandDefinition(
            $"""
             SELECT {Columns}
               FROM prov.tracker_bindings
              WHERE state = '{BindingStates.Active}' AND rotates_at <= @Now
              ORDER BY rotates_at
              LIMIT @Limit
                FOR UPDATE SKIP LOCKED;
             """,
            new { Now = now, Limit = limit },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }
}
