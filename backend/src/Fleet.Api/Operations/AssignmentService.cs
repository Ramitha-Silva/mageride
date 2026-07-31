using Dapper;
using MageRide.Fleet.Configuration;
using MageRide.Fleet.Domain;
using MageRide.Fleet.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MageRide.Fleet.Operations;

/// <summary>
/// A driver being put on a vehicle for a window of time (US-13.2, AL-23).
/// </summary>
/// <param name="DriverId">
/// The driver's <c>iam.users.id</c>. Either this or <paramref name="DriverPhone"/> — US-13.2 has
/// the operator identify a driver "by User ID / phone", and an operator standing in a depot has the
/// number, not the ULID.
/// </param>
/// <param name="From">
/// When the assignment starts conferring the right to drive. May be in the future: a relief driver
/// booked on Monday for Thursday's shift must not be able to take the bus out on Monday.
/// </param>
/// <param name="To">When it stops, or <see langword="null"/> for a permanent driver.</param>
public sealed record AssignDriverCommand(
    string? DriverId, string? DriverPhone, string? VehicleId, DateTimeOffset? From, DateTimeOffset? To);

/// <summary>Driver ↔ vehicle assignment for the org's fleet (US-13.2, US-13.8, US-13.9).</summary>
public interface IAssignmentService
{
    Task<FleetAssignment> AssignAsync(
        Guid fleetId, AssignDriverCommand command, CancellationToken cancellationToken);

    Task<IReadOnlyList<FleetAssignment>> ListAsync(
        Guid fleetId, Guid? vehicleId, CancellationToken cancellationToken);

    Task RevokeAsync(Guid fleetId, Guid assignmentId, CancellationToken cancellationToken);
}

/// <summary>
/// <inheritdoc cref="IAssignmentService"/>
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here expires an assignment, and that is the design.</b> US-13.9 says an assignment
/// "auto-expires"; <c>registry.driver_eligible_vehicles</c> (migration 0314) evaluates the window
/// at read time, so the driver's Driver App stops offering the vehicle the instant
/// <c>expires_at</c> passes — with no sweep, no job and nobody pressing anything. A sweep would be
/// a second mechanism that could lag or be switched off, and the driver would keep the bus for as
/// long as it did. <c>The_expiry_removes_the_vehicle_from_the_driver…</c> asserts it against the
/// projection dispatch-svc and trip-state-svc read.
/// </para>
/// <para>
/// <b>The overlap rule is the database's.</b> <c>ex_fleet_assign_overlap</c> refuses a second open
/// assignment of one driver to one vehicle whose window overlaps an existing one; consecutive
/// windows are legal, which is how a relief driver is re-hired next month. A pre-check here would
/// lose the race between two managers assigning at once, so the constraint is allowed to fire and
/// its <c>23P01</c> becomes the 409.
/// </para>
/// <para>
/// <b>The driver must exist and must be a driver.</b> A fleet vehicle's assignee goes on to open a
/// <c>trips.sessions</c> row, which references <c>iam.users</c>; assigning a passenger would either
/// fail on a foreign key at some later hour or produce an assignment nobody can act on. The refusal
/// is <c>404 driver-not-found</c> either way — an operator who mistyped a phone number and an
/// operator whose new hire has not installed the app get different words in the detail, and neither
/// is told whether some other person exists behind the id they guessed.
/// </para>
/// </remarks>
internal sealed class AssignmentService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IFleetScopedReader scopedReader,
    IFleetAssignmentRepository assignments,
    IOptions<FleetOptions> options,
    ILogger<AssignmentService> logger) : IAssignmentService
{
    private readonly FleetOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<FleetAssignment> AssignAsync(
        Guid fleetId, AssignDriverCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (command.DriverId is null && command.DriverPhone is null)
        {
            errors["driverId"] = ["Name the driver by driverId or by driverPhone (US-13.2)."];
        }

        var vehicleId = RequestIdentifier(errors, command.VehicleId, "vehicleId");
        var driverId = command.DriverId is null ? (Guid?)null : RequestIdentifier(errors, command.DriverId, "driverId");

        // `from` is required by the contract, and a missing one is a validation failure rather than
        // a default of now(): "when does this driver start" is a decision, and guessing it would
        // silently hand a bus to a relief driver a week early.
        if (command.From is null)
        {
            errors["from"] = ["from is required — an assignment is time-bounded (AL-23)."];
        }

        if (command.To is { } to && command.From is { } from && to <= from)
        {
            errors["to"] = ["to must be after from."];
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        // The transaction stays as the service's login role — the fleet reader holds SELECT only —
        // but it still has to say which organisation it acts for, or the read-back through
        // `registry.fleet_assignments_fleet` matches nothing and the caller is told their own
        // assignment does not exist. `FleetScope.ApplyFleetIdAsync` is that half on its own.
        await FleetScope.ApplyFleetIdAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fleetId, cancellationToken);

        var driver = driverId is { } byId
            ? await FindDriverByIdAsync(unitOfWork, byId, cancellationToken)
            : await FindDriverByPhoneAsync(unitOfWork, command.DriverPhone!, cancellationToken);

        if (driver is null)
        {
            throw new MageRideException(
                MageRideErrors.DriverNotFound,
                "No MageRide account matches that driver. US-13.2 assigns an existing Driver App user; they have to "
                + "sign up before they can be given a vehicle.");
        }

        Guid assignmentId;

        try
        {
            assignmentId = await assignments.CreateAsync(
                unitOfWork.Connection,
                unitOfWork.Transaction,
                fleetId,
                vehicleId,
                driver.Value,
                command.From!.Value,
                command.To,
                cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ExclusionViolation)
        {
            throw new MageRideException(
                MageRideErrors.Conflict,
                "That driver already holds an assignment on this vehicle over part of the same period. Revoke it, or "
                + "choose a window that does not overlap.");
        }

        // No row means the vehicle is not on this org's roster — the INSERT's own `WHERE EXISTS`.
        if (assignmentId == Guid.Empty)
        {
            throw new MageRideException(
                MageRideErrors.VehicleNotFound, "This vehicle is not in the organisation's fleet.");
        }

        var assigned = await assignments.FindAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fleetId, assignmentId, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Fleet {FleetId} assigned driver {DriverId} to vehicle {VehicleId} from {From} until {Until}.",
            fleetId,
            driver,
            vehicleId,
            command.From,
            command.To is null ? "revoked" : command.To.ToString());

        return assigned ?? throw new MageRideException(
            MageRideErrors.InternalError, "The assignment was written and could not be read back.");
    }

    public Task<IReadOnlyList<FleetAssignment>> ListAsync(
        Guid fleetId, Guid? vehicleId, CancellationToken cancellationToken) =>
        scopedReader.ReadAsync(
            fleetId,
            (connection, transaction) => assignments.ListAsync(
                connection, transaction, fleetId, vehicleId, _options.MaxPageSize, cancellationToken),
            cancellationToken);

    public async Task RevokeAsync(Guid fleetId, Guid assignmentId, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var revoked = await assignments.RevokeAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fleetId, assignmentId, cancellationToken);

        if (!revoked)
        {
            // One 404 for "no such assignment" and for "already revoked". The second is a client
            // acting on a stale list, and telling the two apart would confirm to somebody guessing
            // ids that they had found a real one.
            throw new MageRideException(
                MageRideErrors.NotFound, "No open assignment of this organisation has that id.");
        }

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Fleet {FleetId} revoked assignment {AssignmentId}; the driver loses the vehicle at once (US-13.8). "
            + "An in-flight session is left to end normally.",
            fleetId,
            assignmentId);
    }

    /// <summary>The account, if it is a driver's.</summary>
    /// <remarks>
    /// AL-06 makes permissions the union of <c>iam.user_roles</c>, so the check is over both the
    /// primary column and the grant table: somebody who signed up as a passenger and later became a
    /// driver holds <c>driver</c> in the second and something else in the first.
    /// </remarks>
    private static Task<Guid?> FindDriverByIdAsync(
        IUnitOfWork unitOfWork, Guid driverId, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            """
            SELECT u.id FROM iam.users u
             WHERE u.id = @DriverId
               AND NOT u.is_blocked
               AND (u.role = 'driver'
                    OR EXISTS (SELECT 1 FROM iam.user_roles r
                                WHERE r.user_id = u.id AND r.role = 'driver'));
            """,
            new { DriverId = driverId },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));

    private static Task<Guid?> FindDriverByPhoneAsync(
        IUnitOfWork unitOfWork, string phone, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            """
            SELECT u.id FROM iam.users u
             WHERE u.phone = @Phone
               AND NOT u.is_blocked
               AND (u.role = 'driver'
                    OR EXISTS (SELECT 1 FROM iam.user_roles r
                                WHERE r.user_id = u.id AND r.role = 'driver'));
            """,
            new { Phone = phone.Trim() },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));

    private static Guid RequestIdentifier(Dictionary<string, string[]> errors, string? value, string field)
    {
        if (MageRide.Shared.Primitives.Ulids.TryParse(value, out var parsed) && parsed != Guid.Empty)
        {
            return parsed;
        }

        errors[field] = [$"{field} is required and must be a ULID or a UUID."];

        return Guid.Empty;
    }
}
