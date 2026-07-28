using System.Security.Cryptography;
using System.Text;
using MageRide.Registry.Vehicles;
using MageRide.Shared.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Registry.Endpoints;

/// <summary>
/// <c>/v1/internal/vehicles</c> — the OnePay merchant bind a vehicle's approval earns (D-11).
/// </summary>
/// <remarks>
/// D3' §0 puts the whole <c>/v1/internal/**</c> family on service-to-service mTLS and the API
/// gateway already refuses the prefix at the edge (C008). Until a mesh exists (C042) the
/// in-cluster hop is guarded by a shared secret; without <c>Registry:InternalApiKey</c> the route
/// is not mapped at all, so a deployment that forgets it gets 404s rather than an open door — the
/// same shape as ride-svc's internal routes (C022).
/// </remarks>
public static class InternalVehicleEndpoints
{
    /// <summary>Carries <c>Registry:InternalApiKey</c>. Replaced by the mTLS peer identity in C042.</summary>
    public const string ApiKeyHeader = "X-MageRide-Internal-Key";

    public static IEndpointRouteBuilder MapInternalVehicleEndpoints(
        this IEndpointRouteBuilder endpoints, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        // AllowAnonymous because the caller is a service, not a user: there is no bearer to
        // present and the kernel's fallback policy would otherwise 401 every call. The filter is
        // what actually authenticates it.
        var internalVehicles = endpoints.MapGroup("/v1/internal/vehicles")
            .WithTags("vehicles")
            .AllowAnonymous()
            .AddEndpointFilter(new RegistryInternalApiKeyFilter(apiKey));

        internalVehicles.MapPost("/{vehicleId}/merchant", BindMerchantAsync).WithName("bindOnepayMerchant");

        return endpoints;
    }

    private static async Task<Ok<BindMerchantResponse>> BindMerchantAsync(
        string vehicleId, BindMerchantBody? body, IMerchantService merchants, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(merchants);

        var id = VehicleEndpoints.RequireVehicleId(vehicleId);

        var payout = await merchants.BindMerchantAsync(
            new BindMerchantCommand(id, body?.MerchantId, body?.MerchantRef), cancellationToken);

        return TypedResults.Ok(new BindMerchantResponse(id.ToString(), payout.OnepayMerchantId));
    }
}

/// <summary>Refuses a request that does not carry the internal shared secret.</summary>
/// <remarks>
/// Fixed-time comparison: the header is a secret, and an early-exit <c>string ==</c> leaks its
/// prefix to anybody willing to time a few thousand requests. Same shape as ride-svc's filter.
/// </remarks>
internal sealed class RegistryInternalApiKeyFilter(string apiKey) : IEndpointFilter
{
    private readonly byte[] _expected = Encoding.UTF8.GetBytes(apiKey);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var presented = context.HttpContext.Request.Headers[InternalVehicleEndpoints.ApiKeyHeader].ToString();

        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _expected))
        {
            throw new MageRideException(
                MageRideErrors.Unauthorized, "This route is service-to-service only (D3' §0).");
        }

        return await next(context);
    }
}
