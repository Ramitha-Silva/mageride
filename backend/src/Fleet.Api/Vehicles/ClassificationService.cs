using MageRide.Fleet.Domain;
using MageRide.Fleet.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;

namespace MageRide.Fleet.Vehicles;

/// <summary>
/// "Service payment" — Free or Paid — for one of the org's Mode B vehicles (AL-24 item 16b, AL-51).
/// </summary>
public interface IClassificationService
{
    Task<FleetVehicle> SetAsync(
        Guid fleetId,
        Guid vehicleId,
        string modeBBilling,
        long? defaultMonthlyFareMinor,
        CancellationToken cancellationToken);
}

/// <summary>
/// <inheritdoc cref="IClassificationService"/>
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the gate the payout profile exists for.</b> BR-31.1: a vehicle cannot be set Service
/// payment = Paid while the organisation has no <c>verified</c> profile, and the refusal is
/// <c>409 payout-profile-not-verified</c>. Without it an operator could start collecting monthly
/// fares with no approved account to collect them into, and subscription-svc would have no
/// <c>payTo</c> to render on the passenger's pay sheet.
/// </para>
/// <para>
/// <b>The gate and the write are one transaction.</b> Reading the profile, deciding, and then
/// updating would leave a window in which an officer rejected the profile between the two — and
/// the vehicle would come out Paid against an account nobody approved. The <c>SELECT</c> is not
/// entered through <see cref="IFleetScopedReader"/> for the same reason: a read-only role cannot
/// carry the write, and this read returns nothing to the caller — it is a gate, not a projection.
/// The organisation is still the one the endpoint filter resolved from
/// <c>iam.fleet_members</c>, and the <c>UPDATE</c> is itself guarded on fleet membership.
/// </para>
/// <para>
/// <b>Free is not gated.</b> An organisation with no payout profile at all can run its whole fleet
/// as Free — an office shuttle collects nothing — and demanding a bank account to say so would be
/// a gate on the wrong thing.
/// </para>
/// </remarks>
internal sealed class ClassificationService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IFleetVehicleRepository vehicles,
    IPayoutProfileRepository profiles,
    ILogger<ClassificationService> logger) : IClassificationService
{
    public async Task<FleetVehicle> SetAsync(
        Guid fleetId,
        Guid vehicleId,
        string modeBBilling,
        long? defaultMonthlyFareMinor,
        CancellationToken cancellationToken)
    {
        if (!ModeBBilling.All.Contains(modeBBilling))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["modeBBilling"] = ["modeBBilling must be 'free' or 'paid' (\"Service payment\" in the UI, AL-51)."],
            });
        }

        var paid = string.Equals(modeBBilling, ModeBBilling.Paid, StringComparison.Ordinal);
        var fare = RequireFare(paid, defaultMonthlyFareMinor);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        // The transaction stays as the service's login role — the fleet reader holds SELECT only
        // and cannot carry the UPDATE below — but it still has to say which organisation it is
        // acting for, or `registry.fleet_vehicles_fleet` matches nothing and an owner is told
        // their own vehicle does not exist.
        await FleetScope.ApplyFleetIdAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fleetId, cancellationToken);

        var vehicle = await vehicles.FindAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fleetId, vehicleId, cancellationToken)
            ?? throw new MageRideException(
                MageRideErrors.VehicleNotFound, "This vehicle is not in the organisation's fleet.");

        // A Mode A bus has no subscribers and `registry.vehicles.mode_b_billing` is NULL for
        // Mode A and C by design (AL-24). Refusing here rather than writing a value the column
        // documents as meaningless — and 400 rather than 409, because the request is wrong about
        // the vehicle it names, not in conflict with a state that might change.
        if (!string.Equals(vehicle.Mode, FleetModes.Private, StringComparison.Ordinal))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["modeBBilling"] = ["Service payment applies to Mode B vehicles only; this one is Mode " + vehicle.Mode + "."],
            });
        }

        if (paid)
        {
            var verified = await profiles.FindVerifiedAsync(
                unitOfWork.Connection, unitOfWork.Transaction, fleetId, cancellationToken);

            if (verified is null)
            {
                throw new MageRideException(
                    MageRideErrors.PayoutProfileNotVerified,
                    "A Verification Officer must verify the organisation's bank and payout profile before a vehicle "
                    + "can be set to Paid (BR-31.1).");
            }
        }

        var updated = await vehicles.SetClassificationAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fleetId, vehicleId, modeBBilling, fare, cancellationToken)
            ?? throw new MageRideException(
                MageRideErrors.VehicleNotFound, "This vehicle is not in the organisation's fleet.");

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Fleet {FleetId} set vehicle {VehicleId} Service payment = {ModeBBilling} (AL-51 label; mode_b_billing column).",
            fleetId,
            vehicleId,
            modeBBilling);

        return updated;
    }

    /// <summary>
    /// The fare, validated against the column rather than against the contract.
    /// </summary>
    /// <remarks>
    /// <c>fleet.yaml</c> types <c>defaultMonthlyFareMinor</c> as <c>int64</c> and
    /// <c>registry.vehicles.default_monthly_fare_minor</c> is <c>INTEGER</c>. The narrower of the
    /// two is what a row can hold, so a wider number is a <c>400</c> here rather than a <c>22003</c>
    /// from Postgres with a message about integer range.
    /// </remarks>
    private static int? RequireFare(bool paid, long? fareMinor)
    {
        if (!paid)
        {
            // Silently dropped rather than refused: a client that sends the pair and then switches
            // the toggle to Free is not making a mistake, and the column is nulled either way.
            return null;
        }

        if (fareMinor is not { } minor || minor <= 0)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["defaultMonthlyFareMinor"] =
                    ["A Paid vehicle needs a default monthly fare in cents, greater than zero."],
            });
        }

        if (minor > int.MaxValue)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["defaultMonthlyFareMinor"] =
                    [$"The monthly fare is at most {int.MaxValue} cents (registry.vehicles.default_monthly_fare_minor is INTEGER)."],
            });
        }

        return (int)minor;
    }
}
