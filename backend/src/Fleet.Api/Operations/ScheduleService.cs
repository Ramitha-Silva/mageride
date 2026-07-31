using MageRide.Fleet.Configuration;
using MageRide.Fleet.Domain;
using MageRide.Fleet.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MageRide.Fleet.Operations;

/// <summary>A departure an operator is booking for one of the org's vehicles (US-13.11).</summary>
public sealed record CreateScheduleCommand(
    string? VehicleId, string? RouteId, DateTimeOffset? DepartAt, int? NotStartedAlarmMinutes);

/// <summary>Per-vehicle scheduled departures and their not-started alarms (US-13.11, SCR-FP-008).</summary>
public interface IScheduleService
{
    Task<FleetSchedule> CreateAsync(
        Guid fleetId, Guid createdBy, CreateScheduleCommand command, CancellationToken cancellationToken);

    /// <summary>The org's upcoming departures, and the recent ones an alarm has been raised on.</summary>
    Task<IReadOnlyList<FleetSchedule>> ListAsync(
        Guid fleetId, DateTimeOffset from, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IScheduleService"/>
internal sealed class ScheduleService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IFleetScopedReader scopedReader,
    IFleetScheduleRepository schedules,
    IOptions<FleetOptions> options,
    TimeProvider clock,
    ILogger<ScheduleService> logger) : IScheduleService
{
    /// <summary><c>fleet.yaml</c>'s bound on <c>notStartedAlarmMinutes</c>, and 0314's CHECK.</summary>
    private const int MinAlarmMinutes = 1;
    private const int MaxAlarmMinutes = 120;

    /// <summary>The contract's default: ten minutes after the booked departure.</summary>
    private const short DefaultAlarmMinutes = 10;

    private readonly FleetOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<FleetSchedule> CreateAsync(
        Guid fleetId, Guid createdBy, CreateScheduleCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (!MageRide.Shared.Primitives.Ulids.TryParse(command.VehicleId, out var vehicleId)
            || vehicleId == Guid.Empty)
        {
            errors["vehicleId"] = ["vehicleId is required and must be a ULID or a UUID."];
        }

        Guid? routeId = null;

        if (command.RouteId is { Length: > 0 })
        {
            if (MageRide.Shared.Primitives.Ulids.TryParse(command.RouteId, out var parsed) && parsed != Guid.Empty)
            {
                routeId = parsed;
            }
            else
            {
                errors["routeId"] = ["routeId must be a ULID or a UUID."];
            }
        }

        if (command.DepartAt is null)
        {
            errors["departAt"] = ["departAt is required."];
        }
        else if (command.DepartAt <= _clock.GetUtcNow())
        {
            // A departure in the past would be swept into MISSED by the very next pass and would
            // ring an alarm about a bus that left this morning. Refused rather than accepted and
            // immediately alarmed.
            errors["departAt"] = ["departAt must be in the future."];
        }

        var alarmMinutes = command.NotStartedAlarmMinutes ?? DefaultAlarmMinutes;

        if (alarmMinutes is < MinAlarmMinutes or > MaxAlarmMinutes)
        {
            errors["notStartedAlarmMinutes"] =
                [$"notStartedAlarmMinutes is between {MinAlarmMinutes} and {MaxAlarmMinutes}."];
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        FleetSchedule created;

        try
        {
            created = await schedules.CreateAsync(
                unitOfWork.Connection,
                unitOfWork.Transaction,
                fleetId,
                vehicleId,
                routeId,
                command.DepartAt!.Value,
                (short)alarmMinutes,
                createdBy,
                cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // QuerySingleAsync over an INSERT ... SELECT that inserted nothing: the vehicle is not
            // on this org's roster, which the statement checks rather than trusting a prior read.
            throw new MageRideException(
                MageRideErrors.VehicleNotFound, "This vehicle is not in the organisation's fleet.");
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // ux_fleet_schedules_slot. Two managers entering the 06:10 from the depot minutes apart
            // are two genuinely different requests, so an Idempotency-Key does not catch it and the
            // index does.
            throw new MageRideException(
                MageRideErrors.Conflict, "This vehicle already has a departure booked at that time.");
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["routeId"] = ["No such route."],
            });
        }

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Fleet {FleetId} scheduled vehicle {VehicleId} to depart at {DepartAt}; the assigned driver's app rings "
            + "{AlarmMinutes} minute(s) later if no session has opened (US-13.11).",
            fleetId,
            vehicleId,
            command.DepartAt,
            alarmMinutes);

        return created;
    }

    public Task<IReadOnlyList<FleetSchedule>> ListAsync(
        Guid fleetId, DateTimeOffset from, CancellationToken cancellationToken) =>
        scopedReader.ReadAsync(
            fleetId,
            (connection, transaction) => schedules.ListAsync(
                connection, transaction, fleetId, from, _options.MaxPageSize, cancellationToken),
            cancellationToken);
}
