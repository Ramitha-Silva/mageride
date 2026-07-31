using Dapper;
using MageRide.Safety.Domain;
using MageRide.Shared.Persistence;

namespace MageRide.Safety.Persistence;

/// <summary>One row of <c>safety.vehicle_reports</c> (migrations 0903 + 0905).</summary>
public sealed record VehicleReport(
    Guid Id,
    Guid ReporterId,
    Guid VehicleId,
    Guid? RideId,
    Guid? DriverId,
    string Reason,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt,
    Guid? ResolvedBy,
    string? ResolutionNote);

/// <summary>
/// <c>safety.vehicle_reports</c> and <c>safety.blocked_drivers</c> — US-12.5, US-12.6, US-12.10.
/// </summary>
public interface IReportRepository
{
    /// <summary>Files a report in <c>PENDING</c>, inside the transaction that raises its event.</summary>
    Task<VehicleReport> CreateAsync(
        IUnitOfWork unitOfWork,
        Guid reporterId,
        Guid vehicleId,
        Guid? rideId,
        Guid? driverId,
        string reason,
        CancellationToken cancellationToken);

    Task<VehicleReport?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a report out of <c>PENDING</c>.
    /// </summary>
    /// <remarks>
    /// A guarded <c>UPDATE … WHERE status = 'PENDING'</c>: two moderators opening the same report
    /// resolve one decision between them, and the loser is told the report has already been
    /// resolved rather than overwriting who decided it. <see langword="null"/> means it had already
    /// moved.
    /// </remarks>
    Task<VehicleReport?> ResolveAsync(
        IUnitOfWork unitOfWork,
        Guid id,
        string status,
        Guid? resolvedBy,
        string? note,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>US-12.6's tally: CONFIRMED reports against one vehicle (<c>ix_vreports_confirmed</c>).</summary>
    Task<int> CountConfirmedAsync(
        IUnitOfWork unitOfWork, Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>The moderation inbox (SCR-AP-005), oldest first — the queue admin-bff draws.</summary>
    Task<IReadOnlyList<VehicleReport>> ListPendingAsync(
        DateTimeOffset? before, int limit, CancellationToken cancellationToken);

    /// <summary>US-12.10. <see langword="false"/> when the pair was already blocked.</summary>
    Task<bool> BlockAsync(Guid passengerId, Guid driverId, CancellationToken cancellationToken);

    /// <summary><see langword="false"/> when there was nothing to clear.</summary>
    Task<bool> UnblockAsync(Guid passengerId, Guid driverId, CancellationToken cancellationToken);

    Task<bool> IsBlockedAsync(Guid passengerId, Guid driverId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IReportRepository"/>
internal sealed class ReportRepository(INpgsqlConnectionFactory connections) : IReportRepository
{
    private readonly INpgsqlConnectionFactory _connections =
        connections ?? throw new ArgumentNullException(nameof(connections));

    private const string Columns =
        """
        id, reporter_id, vehicle_id, ride_id, driver_id, reason, status, created_at,
        resolved_at, resolved_by, resolution_note
        """;

    public async Task<VehicleReport> CreateAsync(
        IUnitOfWork unitOfWork,
        Guid reporterId,
        Guid vehicleId,
        Guid? rideId,
        Guid? driverId,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        return await unitOfWork.Connection.QuerySingleAsync<VehicleReport>(
            new CommandDefinition(
                $"""
                 INSERT INTO safety.vehicle_reports (reporter_id, vehicle_id, ride_id, driver_id, reason)
                 VALUES (@ReporterId, @VehicleId, @RideId, @DriverId, @Reason)
                 RETURNING {Columns};
                 """,
                new { ReporterId = reporterId, VehicleId = vehicleId, RideId = rideId, DriverId = driverId, Reason = reason },
                unitOfWork.Transaction,
                cancellationToken: cancellationToken));
    }

    public async Task<VehicleReport?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<VehicleReport>(
            new CommandDefinition(
                $"SELECT {Columns} FROM safety.vehicle_reports WHERE id = @Id;",
                new { Id = id },
                cancellationToken: cancellationToken));
    }

    public async Task<VehicleReport?> ResolveAsync(
        IUnitOfWork unitOfWork,
        Guid id,
        string status,
        Guid? resolvedBy,
        string? note,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        return await unitOfWork.Connection.QuerySingleOrDefaultAsync<VehicleReport>(
            new CommandDefinition(
                $"""
                 UPDATE safety.vehicle_reports
                    SET status = @Status,
                        resolved_at = @At,
                        resolved_by = @ResolvedBy,
                        resolution_note = @Note
                  WHERE id = @Id AND status = '{VehicleReportStatuses.Pending}'
                 RETURNING {Columns};
                 """,
                new { Id = id, Status = status, At = at, ResolvedBy = resolvedBy, Note = note },
                unitOfWork.Transaction,
                cancellationToken: cancellationToken));
    }

    public Task<int> CountConfirmedAsync(
        IUnitOfWork unitOfWork, Guid vehicleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        // Inside the caller's transaction, after the resolve: the third confirmation and the count
        // that makes it the third have to be one atomic fact, or two concurrent confirmations both
        // read two and neither delists.
        return unitOfWork.Connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                $"""
                 SELECT count(*)::int FROM safety.vehicle_reports
                  WHERE vehicle_id = @VehicleId AND status = '{VehicleReportStatuses.Confirmed}';
                 """,
                new { VehicleId = vehicleId },
                unitOfWork.Transaction,
                cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<VehicleReport>> ListPendingAsync(
        DateTimeOffset? before, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        // `ix_vreports_pending` is (created_at DESC) WHERE status = 'PENDING' — this page exactly.
        var rows = await connection.QueryAsync<VehicleReport>(
            new CommandDefinition(
                $"""
                 SELECT {Columns}
                   FROM safety.vehicle_reports
                  WHERE status = '{VehicleReportStatuses.Pending}'
                    AND (@Before::timestamptz IS NULL OR created_at < @Before)
                  ORDER BY created_at DESC
                  LIMIT @Limit;
                 """,
                new { Before = before, Limit = limit },
                cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<bool> BlockAsync(Guid passengerId, Guid driverId, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        // ON CONFLICT DO NOTHING on `ux_blocked_drivers_pair`: blocking somebody twice is not an
        // error, it is a client that tapped twice, and the row already says what it needs to.
        var inserted = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO safety.blocked_drivers (passenger_id, driver_id)
                VALUES (@PassengerId, @DriverId)
                ON CONFLICT (passenger_id, driver_id) DO NOTHING;
                """,
                new { PassengerId = passengerId, DriverId = driverId },
                cancellationToken: cancellationToken));

        return inserted == 1;
    }

    public async Task<bool> UnblockAsync(Guid passengerId, Guid driverId, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        var deleted = await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM safety.blocked_drivers WHERE passenger_id = @PassengerId AND driver_id = @DriverId;",
                new { PassengerId = passengerId, DriverId = driverId },
                cancellationToken: cancellationToken));

        return deleted == 1;
    }

    public async Task<bool> IsBlockedAsync(Guid passengerId, Guid driverId, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT EXISTS (SELECT 1 FROM safety.blocked_drivers
                                WHERE passenger_id = @PassengerId AND driver_id = @DriverId);
                """,
                new { PassengerId = passengerId, DriverId = driverId },
                cancellationToken: cancellationToken));
    }
}

/// <summary>One outcome of a proxy location request (migration 0904).</summary>
public sealed record LocationRequestAuditRow(
    Guid Id, Guid BookerId, byte[] RiderPhoneHash, Guid RequestId, string Decision, DateTimeOffset Ts);

/// <summary>
/// The P-12 forensic read over <c>safety.location_request_audit</c>.
/// </summary>
/// <remarks>
/// <b>Read-only, and that is the whole boundary.</b> The rows are written by <b>ride-svc</b> (C037)
/// inside the transaction that resolves each request — which is the only place they can be correct,
/// because a decision and its audit row have to commit together. What this service adds is the read
/// the table exists for: "this booker keeps pinging somebody who keeps declining" is the abuse
/// pattern P-12 names, and until now nothing could ask the question. A second writer here would
/// double-count every outcome and leave the two copies to disagree — recorded in the C052 handoff.
/// </remarks>
public interface ILocationRequestAuditRepository
{
    /// <summary>One booker's outcomes, newest first — the abuse investigation P-12 describes.</summary>
    Task<IReadOnlyList<LocationRequestAuditRow>> ListForBookerAsync(
        Guid bookerId, DateTimeOffset? since, int limit, CancellationToken cancellationToken);

    /// <summary>How many of a booker's recent requests ended each way.</summary>
    Task<IReadOnlyDictionary<string, int>> SummariseForBookerAsync(
        Guid bookerId, DateTimeOffset since, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ILocationRequestAuditRepository"/>
internal sealed class LocationRequestAuditRepository(INpgsqlConnectionFactory connections)
    : ILocationRequestAuditRepository
{
    private readonly INpgsqlConnectionFactory _connections =
        connections ?? throw new ArgumentNullException(nameof(connections));

    public async Task<IReadOnlyList<LocationRequestAuditRow>> ListForBookerAsync(
        Guid bookerId, DateTimeOffset? since, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        // `ix_locreq_audit_booker` is (booker_id, ts DESC) and 0904's header names this query.
        var rows = await connection.QueryAsync<LocationRequestAuditRow>(
            new CommandDefinition(
                """
                SELECT id, booker_id, rider_phone_hash, request_id, decision, ts
                  FROM safety.location_request_audit
                 WHERE booker_id = @BookerId
                   AND (@Since::timestamptz IS NULL OR ts >= @Since)
                 ORDER BY ts DESC
                 LIMIT @Limit;
                """,
                new { BookerId = bookerId, Since = since, Limit = limit },
                cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<IReadOnlyDictionary<string, int>> SummariseForBookerAsync(
        Guid bookerId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<(string Decision, int Count)>(
            new CommandDefinition(
                """
                SELECT decision, count(*)::int AS count
                  FROM safety.location_request_audit
                 WHERE booker_id = @BookerId AND ts >= @Since
                 GROUP BY decision;
                """,
                new { BookerId = bookerId, Since = since },
                cancellationToken: cancellationToken));

        return rows.ToDictionary(static row => row.Decision, static row => row.Count, StringComparer.Ordinal);
    }
}
