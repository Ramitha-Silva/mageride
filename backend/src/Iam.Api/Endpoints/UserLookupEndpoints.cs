using System.Security.Cryptography;
using System.Text;
using MageRide.Iam.Profiles;
using MageRide.Shared.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Iam.Endpoints;

/// <summary>
/// <c>GET /v1/users/lookup</c> — the proxy-booking registration check (P-03).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this needs its own guard.</b> D3' §0 puts service-to-service calls on mTLS and the
/// gateway refuses the whole <c>/v1/internal/**</c> family at the edge (C008) — but this route is
/// <em>not</em> under that prefix, and the gateway's <c>iam-users</c> route forwards
/// <c>/v1/users/{**remainder}</c> to this service from the public internet. Left to the contract's
/// <c>mtls</c> declaration alone it would be a registration oracle anybody could query: send a
/// number, learn whether it belongs to a MageRide user. So it is authenticated here, with the
/// same shared secret ride-svc's internal routes use, until C042 lands a mesh.
/// </para>
/// <para>
/// <c>Auth:InternalApiKey</c> unset means the route is <b>not mapped at all</b> — a deployment
/// that forgets it gets 404s and a broken proxy booking, not an open door.
/// </para>
/// </remarks>
public static class UserLookupEndpoints
{
    /// <summary>Carries <c>Auth:InternalApiKey</c>. Replaced by the mTLS peer identity in C042.</summary>
    public const string ApiKeyHeader = "X-MageRide-Internal-Key";

    /// <summary>Names the calling service, for <c>iam.phone_lookups.caller</c>.</summary>
    public const string CallerHeader = "X-MageRide-Service";

    public static IEndpointRouteBuilder MapUserLookupEndpoints(this IEndpointRouteBuilder endpoints, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        endpoints.MapGet("/v1/users/lookup", LookupAsync)
            .WithTags("users")
            .WithName("lookupUserByPhone")
            // AllowAnonymous because the caller is a service and has no bearer to present; the
            // filter below is what actually authenticates it.
            .AllowAnonymous()
            .AddEndpointFilter(new InternalApiKeyFilter(apiKey));

        return endpoints;
    }

    private static async Task<Ok<LookupUserResponse>> LookupAsync(
        string? phone, HttpContext context, IUserLookupService lookups, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(lookups);

        var caller = context.Request.Headers[CallerHeader].ToString();

        var result = await lookups.LookupAsync(
            phone, string.IsNullOrWhiteSpace(caller) ? null : caller, cancellationToken);

        return TypedResults.Ok(new LookupUserResponse(result.Registered, result.UserId?.ToString()));
    }
}

/// <summary>Refuses a request that does not carry the internal shared secret.</summary>
/// <remarks>
/// Fixed-time comparison: the header is a secret, and an early-exit <c>string ==</c> leaks its
/// prefix to anybody willing to time a few thousand requests. Same shape as ride-svc's filter.
/// </remarks>
internal sealed class InternalApiKeyFilter(string apiKey) : IEndpointFilter
{
    private readonly byte[] _expected = Encoding.UTF8.GetBytes(apiKey);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var presented = context.HttpContext.Request.Headers[UserLookupEndpoints.ApiKeyHeader].ToString();

        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _expected))
        {
            throw new MageRideException(
                MageRideErrors.Unauthorized, "This route is service-to-service only (D3' §0).");
        }

        return await next(context);
    }
}
