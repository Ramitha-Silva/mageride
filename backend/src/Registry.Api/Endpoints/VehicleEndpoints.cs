using MageRide.Registry.Vehicles;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Registry.Endpoints;

/// <summary>
/// <c>/v1/vehicles</c> — the walking skeleton's slice of <c>backend/contracts/registry.yaml</c>.
/// </summary>
/// <remarks>
/// <para>
/// Three of the contract's routes plus one that is not in it. <c>POST /v1/vehicles</c> and
/// <c>GET /v1/vehicles/mine</c> are contract operations narrowed to what a skeleton can honour;
/// <c>POST /v1/vehicles/{vehicleId}/select-live</c> is new — US-9.6 and US-9.7 require a single
/// selected vehicle and no D3' endpoint sets one (C021 handoff, micro-change-set).
/// </para>
/// <para>
/// Everything else the contract declares — the four onboarding-step routes, status polling,
/// deactivate, Mode B sharing, subscribers, share requests, device binding and the OnePay
/// merchant bind — is C028/C029 and is left unmapped rather than stubbed.
/// </para>
/// <para>
/// Every route demands the <c>driver</c> role. Opening the Driver App does not grant it (C020
/// decision 4): a passenger who signs in there carries <c>app=driver, role=passenger</c> and is
/// refused here, which is deny-by-default working as intended.
/// </para>
/// </remarks>
public static class VehicleEndpoints
{
    public static IEndpointRouteBuilder MapVehicleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var vehicles = endpoints.MapGroup("/v1/vehicles")
            .WithTags("vehicles")
            .RequireMageRideRole(MageRideRoles.Driver);

        vehicles.MapPost("/", RegisterAsync).WithName("registerVehicle");
        vehicles.MapGet("/mine", ListMineAsync).WithName("listMyVehicles");
        vehicles.MapPost("/{vehicleId}/select-live", SelectLiveAsync).WithName("selectLiveVehicle");

        return endpoints;
    }

    private static async Task<Created<RegisterVehicleResponse>> RegisterAsync(
        RegisterVehicleBody? body, HttpContext context, IVehicleService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var driverId = context.User.RequireSubjectId();

        var vehicle = await service.RegisterAsync(
            new RegisterVehicleCommand(driverId, body?.RegistrationNumber, body?.VehicleType, body?.Mode, body?.DriverName),
            cancellationToken);

        return TypedResults.Created($"/v1/vehicles/{vehicle.Id}", RegisterVehicleResponse.From(vehicle));
    }

    private static async Task<Ok<MyVehiclesResponse>> ListMineAsync(
        HttpContext context, IVehicleService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var owned = await service.ListMineAsync(context.User.RequireSubjectId(), cancellationToken);

        return TypedResults.Ok(new MyVehiclesResponse([.. owned.Select(VehicleSummaryResponse.From)]));
    }

    private static async Task<Ok<LiveSelectionResponse>> SelectLiveAsync(
        string vehicleId, HttpContext context, IVehicleService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var selection = await service.SelectLiveAsync(
            context.User.RequireSubjectId(), RequireVehicleId(vehicleId), cancellationToken);

        return TypedResults.Ok(LiveSelectionResponse.From(selection));
    }

    /// <summary>
    /// Parses the path segment. A malformed id is <c>404 vehicle-not-found</c> rather than a 400:
    /// the contract types it as an opaque ULID-or-UUID, so "not a well-formed identifier" and "no
    /// such vehicle" are the same answer to a caller and telling them apart leaks nothing useful.
    /// </summary>
    internal static Guid RequireVehicleId(string? vehicleId) =>
        Guid.TryParse(vehicleId, out var parsed)
            ? parsed
            : throw new MageRideException(MageRideErrors.VehicleNotFound, $"No vehicle '{vehicleId}'.");
}
