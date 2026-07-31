using MageRide.Fleet.Domain;
using MageRide.Fleet.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;

namespace MageRide.Fleet.Vehicles;

/// <summary>A Verification Officer's decision on one fleet vehicle, and the paperwork behind it.</summary>
public sealed record VehicleDecision(
    FleetVehicle Vehicle, IReadOnlyList<VehicleDocumentSlot> Slots, string DocsStatus);

/// <summary>
/// The AL-50 approval gate: a fleet vehicle reaches APPROVED only when every document slot its
/// mode requires is verified (US-13.6, US-27.3, extends AL-10).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this lives in fleet-svc at all.</b> AL-50 says "registry-svc blocks
/// <c>status='APPROVED'</c> until every required doc is verified", and registry-svc's approval path
/// is AL-30's four-step Mode C wizard — which refuses a Mode A/B vehicle outright ("in-app vehicle
/// onboarding is Mode C only") and derives its verdict from <c>registry.onboarding_steps</c>, a
/// table a fleet vehicle has no rows in. So the sentence names a service that structurally cannot
/// hold the gate for the vehicles the sentence is about. The gate is here, over the same column,
/// with the same effect, and the divergence is raised in the C059 handoff.
/// </para>
/// <para>
/// <b>Read the documents, do not trust a stored verdict.</b> The gate re-derives every slot from
/// <c>registry.documents</c> inside the transaction that writes the status, so a permit that
/// expired between the officer opening the queue item and pressing Approve stops the approval. A
/// <c>docs_status</c> column would have been a copy of this answer, made earlier.
/// </para>
/// <para>
/// <b>Rejection is ungated.</b> An officer refusing a vehicle because its insurance is a photograph
/// of a different vehicle must not be told they cannot, on the grounds that the insurance is not
/// verified.
/// </para>
/// </remarks>
public interface IVehicleApprovalService
{
    /// <summary>Everything the officer's queue detail shows for one fleet vehicle.</summary>
    Task<VehicleDecision> ReadAsync(Guid fleetId, Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>Approves it, or refuses with the slots that are not settled.</summary>
    Task<VehicleDecision> ApproveAsync(Guid fleetId, Guid vehicleId, CancellationToken cancellationToken);

    Task<VehicleDecision> RejectAsync(
        Guid fleetId, Guid vehicleId, string reason, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IVehicleApprovalService"/>
internal sealed class VehicleApprovalService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IFleetVehicleRepository vehicles,
    IVehicleDocumentRepository documents,
    ILogger<VehicleApprovalService> logger) : IVehicleApprovalService
{
    public async Task<VehicleDecision> ReadAsync(
        Guid fleetId, Guid vehicleId, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        // The GUC without the reader role, `FleetScope`'s split: this transaction reads through
        // `registry.fleet_vehicles_fleet`, which is scoped by the setting, and the caller is an
        // internal service rather than a fleet member — so dropping to the read-only role would
        // buy nothing and would stop the two decision paths sharing this method.
        await FleetScope.ApplyFleetIdAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fleetId, cancellationToken);

        var decision = await ReadDecisionAsync(unitOfWork, fleetId, vehicleId, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        return decision;
    }

    public async Task<VehicleDecision> ApproveAsync(
        Guid fleetId, Guid vehicleId, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        await FleetScope.ApplyFleetIdAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fleetId, cancellationToken);

        var current = await ReadDecisionAsync(unitOfWork, fleetId, vehicleId, cancellationToken);

        // The gate, in the same transaction as the write it protects. A required slot that lapsed
        // while the queue item sat there is caught here rather than by whoever notices later.
        if (!VehicleDocumentSlots.AreRequiredSlotsVerified(current.Slots))
        {
            var outstanding = VehicleDocumentSlots.UnverifiedRequiredSlots(current.Slots);

            throw new MageRideException(
                MageRideErrors.DocumentsIncomplete,
                $"This {ModeLabel(current.Vehicle.Mode)} vehicle cannot be approved: "
                + string.Join(", ", outstanding)
                + ". Every required document slot must be verified first (AL-50, US-27.3).");
        }

        var approved = await vehicles.SetStatusAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            fleetId,
            vehicleId,
            FleetVehicleStatuses.Approved,
            rejectionReason: null,
            cancellationToken)
            ?? throw new MageRideException(
                MageRideErrors.VehicleNotFound,
                "This vehicle is not in the organisation's fleet, or has been removed from it.");

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Vehicle {VehicleId} of fleet {FleetId} approved with every required AL-50 slot verified.",
            vehicleId,
            fleetId);

        return current with { Vehicle = approved };
    }

    public async Task<VehicleDecision> RejectAsync(
        Guid fleetId, Guid vehicleId, string reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            // US-2.15's column exists so the operator can be told what to fix. A rejection with no
            // reason is a screen that says "rejected" and nothing else.
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["reason"] = ["A rejection must say why (US-2.15)."],
            });
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        await FleetScope.ApplyFleetIdAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fleetId, cancellationToken);

        var rejected = await vehicles.SetStatusAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            fleetId,
            vehicleId,
            FleetVehicleStatuses.Rejected,
            reason.Trim(),
            cancellationToken)
            ?? throw new MageRideException(
                MageRideErrors.VehicleNotFound,
                "This vehicle is not in the organisation's fleet, or has been removed from it.");

        var decision = await ReadDecisionAsync(unitOfWork, fleetId, vehicleId, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Vehicle {VehicleId} of fleet {FleetId} rejected: {Reason}", vehicleId, fleetId, reason);

        return decision with { Vehicle = rejected };
    }

    private async Task<VehicleDecision> ReadDecisionAsync(
        IUnitOfWork unitOfWork, Guid fleetId, Guid vehicleId, CancellationToken cancellationToken)
    {
        var vehicle = await vehicles.FindAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fleetId, vehicleId, cancellationToken)
            ?? throw new MageRideException(
                MageRideErrors.VehicleNotFound, "This vehicle is not in the organisation's fleet.");

        var held = await documents.ListForVehicleAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fleetId, vehicleId, cancellationToken);

        var fields = await documents.ListFieldsAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            [.. held.Select(document => document.Id)],
            cancellationToken);

        var slots = VehicleDocumentSlots.For(vehicle.Mode, held, fields);

        return new VehicleDecision(vehicle, slots, VehicleDocumentSlots.DocsStatus(slots));
    }

    private static string ModeLabel(string mode) =>
        string.Equals(mode, FleetModes.PublicTransport, StringComparison.Ordinal) ? "Mode A" : "Mode B";
}
