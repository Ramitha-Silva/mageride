using MageRide.Registry.Domain;
using MageRide.Registry.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Logging;

namespace MageRide.Registry.Vehicles;

/// <summary><c>POST /v1/internal/vehicles/{vehicleId}/merchant</c> (D-11).</summary>
public sealed record BindMerchantCommand(Guid VehicleId, string? MerchantId, string? MerchantRef);

/// <summary>
/// The OnePay merchant binding a vehicle's approval earns its driver (D-11).
/// </summary>
/// <remarks>
/// Keyed on the <b>driver</b>, not the vehicle: <c>registry.driver_payouts</c>' primary key is
/// <c>driver_id</c> (0304), because settlement pays a person and a driver with three vehicles has
/// one OnePay account. The route is per vehicle because that is the event that triggers it —
/// "called when a vehicle reaches APPROVED, so that fare settlement has a payee".
/// </remarks>
public interface IMerchantService
{
    /// <summary>
    /// Binds the merchant account. <b>Not named <c>BindAsync</c></b>: Minimal APIs treat any
    /// parameter type carrying a <c>BindAsync</c> method as custom-bound, so a handler taking
    /// this service as a dependency fails to build the route table at start-up with
    /// "BindAsync method found on IMerchantService with incorrect format".
    /// </summary>
    Task<DriverPayout> BindMerchantAsync(BindMerchantCommand command, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IMerchantService"/>
public sealed class MerchantService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IVehicleRepository vehicles,
    IDriverPayoutRepository payouts,
    ILogger<MerchantService> logger) : IMerchantService
{
    /// <summary><c>registry.yaml</c>'s <c>maxLength: 128</c> on both merchant fields.</summary>
    private const int MaxMerchantIdLength = 128;

    public async Task<DriverPayout> BindMerchantAsync(BindMerchantCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var merchantId = command.MerchantId?.Trim();

        if (string.IsNullOrEmpty(merchantId) || merchantId.Length > MaxMerchantIdLength)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["merchantId"] = [$"merchantId is required and must be at most {MaxMerchantIdLength} characters."],
            });
        }

        if (command.MerchantRef is { Length: > MaxMerchantIdLength })
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["merchantRef"] = [$"merchantRef must be at most {MaxMerchantIdLength} characters."],
            });
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var vehicle = await vehicles.FindAsync(
                          unitOfWork.Connection, unitOfWork.Transaction, command.VehicleId, cancellationToken)
                      ?? throw new MageRideException(
                          MageRideErrors.VehicleNotFound, $"No vehicle {command.VehicleId}.");

        var payout = await payouts.BindAsync(
            unitOfWork.Connection, unitOfWork.Transaction, vehicle.OwnerId, merchantId, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        // `merchantRef` is accepted and not stored: registry.driver_payouts (0304) has
        // onepay_merchant_id and nothing else, and inventing a column for a field no reader has
        // would be worse than logging it. Recorded as a contract/schema gap in the C028 handoff.
        logger.LogInformation(
            "Bound OnePay merchant {MerchantId} to driver {DriverId} on approval of vehicle {VehicleId} (ref {MerchantRef})",
            merchantId, vehicle.OwnerId, vehicle.Id, command.MerchantRef);

        return payout;
    }
}
