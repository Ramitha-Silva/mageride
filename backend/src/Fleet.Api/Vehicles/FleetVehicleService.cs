using MageRide.Fleet.Configuration;
using MageRide.Fleet.Domain;
using MageRide.Fleet.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MageRide.Fleet.Vehicles;

/// <summary>One vehicle an operator is adding to the roster (US-13.1, SCR-FP-004).</summary>
public sealed record AddFleetVehicleCommand(
    string RegistrationNumber,
    string VehicleType,
    string Mode,
    string? ModeBBilling,
    long? DefaultMonthlyFareMinor);

/// <summary>A roster entry with AL-50's paperwork verdict attached.</summary>
public sealed record FleetVehicleView(FleetVehicle Vehicle, string DocsStatus);

/// <summary>
/// The org's vehicle roster: adding, listing and removing Mode A/B vehicles (US-13.1, US-13.7).
/// </summary>
public interface IFleetVehicleService
{
    Task<FleetVehicleView> AddAsync(
        Guid fleetId, AddFleetVehicleCommand command, CancellationToken cancellationToken);

    Task<IReadOnlyList<FleetVehicleView>> ListAsync(Guid fleetId, CancellationToken cancellationToken);

    /// <summary>US-13.7's removal, with the assignment cascade it implies.</summary>
    Task RemoveAsync(Guid fleetId, Guid vehicleId, CancellationToken cancellationToken);
}

/// <summary>
/// <inheritdoc cref="IFleetVehicleService"/>
/// </summary>
/// <remarks>
/// <para>
/// <b>The mode fence is checked here and held by the database.</b> AL-03 gives a fleet Mode A
/// and/or Mode B and never Mode C; <c>registry.fleet_vehicles.mode CHECK (mode IN ('A','B'))</c> is
/// the second lock, so a route that forgot this would fail on a constraint rather than create a
/// Mode C fleet vehicle. The refusal is <c>403 mode-not-allowed</c>, not <c>400</c> — the value is
/// real and the surface is wrong, which is the distinction registry-svc draws in the opposite
/// direction for <c>bus</c>.
/// </para>
/// <para>
/// <b>A new vehicle is PENDING and stays PENDING until an officer decides.</b> There is no
/// auto-approval on this surface and there must not be: AL-30's auto-approve is the Mode C wizard's,
/// gated on four steps ocr-svc can settle by itself, and AL-50 puts a Mode A vehicle's route permit
/// — a legal document — in front of a person. <see cref="IVehicleApprovalService"/> is that
/// decision arriving from admin-bff.
/// </para>
/// <para>
/// <b>Removing a vehicle revokes its drivers in the same transaction.</b> US-13.7 says removal
/// "immediately removes it from the fleet and passenger maps"; leaving an open assignment behind
/// would keep the vehicle in <c>registry.driver_eligible_vehicles</c> for its driver and let them
/// start a session on a vehicle the operator has taken off the roster.
/// </para>
/// </remarks>
internal sealed class FleetVehicleService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IFleetScopedReader scopedReader,
    IFleetRepository fleets,
    IFleetVehicleRepository vehicles,
    IFleetAssignmentRepository assignments,
    IVehicleDocumentRepository documents,
    IClassificationService classification,
    IOptions<FleetOptions> options,
    ILogger<FleetVehicleService> logger) : IFleetVehicleService
{
    private readonly FleetOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<FleetVehicleView> AddAsync(
        Guid fleetId, AddFleetVehicleCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var registration = RequireRegistration(command.RegistrationNumber);
        RequireOnboardableType(command.VehicleType);
        RequireFleetMode(command.Mode);

        FleetVehicle added;

        await using (var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken))
        {
            var fleet = await fleets.FindAsync(
                unitOfWork.Connection, unitOfWork.Transaction, fleetId, cancellationToken)
                ?? throw new MageRideException(FleetErrors.FleetNotFound, "No such fleet organisation.");

            try
            {
                // The organisation's owner owns the vehicle, and the organisation's name is the
                // `driver_name` a passenger sees until a driver is assigned. `driver_name` is
                // NOT NULL and is "shown to passengers" (US-2.12): on a bus that is the operator,
                // which is also what is painted on the side of it.
                added = await vehicles.AddAsync(
                    unitOfWork.Connection,
                    unitOfWork.Transaction,
                    fleetId,
                    fleet.OwnerId,
                    registration,
                    command.VehicleType,
                    command.Mode,
                    fleet.Name,
                    cancellationToken);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // D-37's ux_vehicles_regno_active. Two operators claiming one plate is the ordinary
                // case — a bus sold between companies whose previous registration is still live —
                // and it is a 409 rather than a 500.
                throw new MageRideException(
                    MageRideErrors.RegistrationExists,
                    $"A live vehicle is already registered as {registration} (D-37).");
            }

            await unitOfWork.CommitAsync(cancellationToken);
        }

        // The classification is a second transaction on purpose. It is the one write on this surface
        // with a gate of its own (BR-31.1's verified payout profile), and that gate is evaluated
        // inside `IClassificationService` along with the write it protects — folding it in here
        // would either duplicate the gate or move it out of the transaction it belongs to. The
        // vehicle exists either way: a 409 on the classification leaves an unclassified Mode B
        // vehicle on the roster, which is exactly the state SCR-FP-004 renders as "Service payment:
        // not set" and the operator fixes with the toggle.
        if (command.ModeBBilling is { Length: > 0 } billing)
        {
            added = await classification.SetAsync(
                fleetId, added.VehicleId, billing, command.DefaultMonthlyFareMinor, cancellationToken);
        }

        logger.LogInformation(
            "Fleet {FleetId} onboarded {Mode} vehicle {VehicleId} ({Registration}); it is PENDING until a "
            + "Verification Officer approves it and every required AL-50 document slot is verified.",
            fleetId,
            command.Mode,
            added.VehicleId,
            registration);

        // Freshly added, so the slots are all missing — but read rather than assumed, because the
        // same shape answers the list and a divergence between "what a POST returns" and "what a
        // GET returns for the same vehicle" is the kind of thing a portal renders as a flicker.
        return await WithDocsStatusAsync(fleetId, added, cancellationToken);
    }

    public Task<IReadOnlyList<FleetVehicleView>> ListAsync(Guid fleetId, CancellationToken cancellationToken) =>
        scopedReader.ReadAsync(
            fleetId,
            async (connection, transaction) =>
            {
                var roster = await vehicles.ListAsync(
                    connection, transaction, fleetId, _options.MaxPageSize, cancellationToken);

                if (roster.Count == 0)
                {
                    return (IReadOnlyList<FleetVehicleView>)[];
                }

                // One read of every document the org holds for the vehicles on this page, then one
                // read of their fields — two queries for a page rather than two per vehicle. The
                // slot rule is applied in memory, where it is the same code the single-vehicle
                // document screen runs.
                var allDocuments = new List<VehicleDocument>();

                foreach (var vehicle in roster)
                {
                    allDocuments.AddRange(await documents.ListForVehicleAsync(
                        connection, transaction, fleetId, vehicle.VehicleId, cancellationToken));
                }

                var fields = await documents.ListFieldsAsync(
                    connection, transaction, [.. allDocuments.Select(document => document.Id)], cancellationToken);

                return (IReadOnlyList<FleetVehicleView>)
                [
                    .. roster.Select(vehicle => new FleetVehicleView(
                        vehicle,
                        VehicleDocumentSlots.DocsStatus(VehicleDocumentSlots.For(
                            vehicle.Mode,
                            [.. allDocuments.Where(document => document.VehicleId == vehicle.VehicleId)],
                            fields)))),
                ];
            },
            cancellationToken);

    public async Task RemoveAsync(Guid fleetId, Guid vehicleId, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        // Assignments first, then the roster row: after the DELETE the vehicle is no longer this
        // org's and the assignment update's own `fleet_id` predicate would match nothing, leaving
        // drivers holding a vehicle that had left the fleet.
        var revoked = await assignments.RevokeAllForVehicleAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fleetId, vehicleId, cancellationToken);

        var removed = await vehicles.RemoveAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fleetId, vehicleId, cancellationToken);

        if (!removed)
        {
            throw new MageRideException(
                MageRideErrors.VehicleNotFound, "This vehicle is not in the organisation's fleet.");
        }

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Fleet {FleetId} removed vehicle {VehicleId}; {Revoked} open driver assignment(s) ended with it "
            + "(US-13.7/13.8). The registration is free again (D-37).",
            fleetId,
            vehicleId,
            revoked);
    }

    /// <summary>The AL-50 verdict for one vehicle, read through the org's own scope.</summary>
    private Task<FleetVehicleView> WithDocsStatusAsync(
        Guid fleetId, FleetVehicle vehicle, CancellationToken cancellationToken) =>
        scopedReader.ReadAsync(
            fleetId,
            async (connection, transaction) =>
            {
                var held = await documents.ListForVehicleAsync(
                    connection, transaction, fleetId, vehicle.VehicleId, cancellationToken);

                var fields = await documents.ListFieldsAsync(
                    connection, transaction, [.. held.Select(document => document.Id)], cancellationToken);

                return new FleetVehicleView(
                    vehicle,
                    VehicleDocumentSlots.DocsStatus(VehicleDocumentSlots.For(vehicle.Mode, held, fields)));
            },
            cancellationToken);

    /// <summary>
    /// Canonicalises the plate, or refuses it.
    /// </summary>
    /// <remarks>
    /// The same normalisation registry-svc applies, and it has to be: D-37's uniqueness is a unique
    /// index over the stored text, so two writers storing one plate differently bypass it. See
    /// <see cref="FleetRegistrationNumbers"/>.
    /// </remarks>
    internal static string RequireRegistration(string? registrationNumber) =>
        FleetRegistrationNumbers.TryNormalise(registrationNumber, out var normalised)
            ? normalised
            : throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["registrationNumber"] =
                [
                    "registrationNumber is required, is at most 32 characters, and may contain only letters, "
                    + "digits, spaces and hyphens.",
                ],
            });

    internal static void RequireOnboardableType(string? vehicleType)
    {
        if (!FleetVehicleTypes.IsKnown(vehicleType))
        {
            throw new MageRideException(
                MageRideErrors.InvalidVehicleType,
                $"'{vehicleType}' is not a MageRide vehicle type (AL-09). There is no 'car' — it is 'sedan'.");
        }

        // A real type on the wrong surface. US-2.17/2.18 make trains admin-only and give them
        // `POST /v1/admin/trains`; an operator registering one here would put a train on the
        // passenger map that no admin decided to run.
        if (!FleetVehicleTypes.IsFleetOnboardable(vehicleType!))
        {
            throw new MageRideException(
                MageRideErrors.ModeNotAllowed,
                "Trains are administered centrally (US-2.17/2.18) and are not onboarded through the Fleet Portal.");
        }
    }

    internal static void RequireFleetMode(string? mode)
    {
        if (!FleetModes.IsFleetMode(mode))
        {
            throw new MageRideException(
                MageRideErrors.ModeNotAllowed,
                "A fleet operates Mode A (public transport) and/or Mode B (private) vehicles only. Mode C is the "
                + "on-demand plane and a driver's own vehicle is onboarded in the Driver App (AL-03).");
        }
    }
}
