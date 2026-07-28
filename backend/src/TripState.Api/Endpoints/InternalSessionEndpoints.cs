using System.Security.Cryptography;
using System.Text;
using MageRide.Shared.Errors;
using MageRide.TripState.Configuration;
using MageRide.TripState.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace MageRide.TripState.Endpoints;

/// <summary>
/// <c>/v1/internal/sessions</c> — the timer's and the tracker plane's surface.
/// </summary>
/// <remarks>
/// D3' §0 puts the whole <c>/v1/internal/**</c> family on service-to-service mTLS and the gateway
/// refuses the prefix at the edge (C008); this is the interim until C042 lands a mesh. Without
/// <c>TripState:InternalApiKey</c> the routes are not mapped at all, so a deployment that forgets
/// it gets 404s rather than an open door — and the visible symptom is that ignition auto-sessions
/// stop happening, not that anything unauthenticated can end a journey.
/// </remarks>
public static class InternalSessionEndpoints
{
    /// <summary>Carries <c>TripState:InternalApiKey</c>. Replaced by the mTLS peer identity in C042.</summary>
    public const string ApiKeyHeader = "X-MageRide-Internal-Key";

    /// <summary>ACC on.</summary>
    public const string IgnitionOn = "on";

    /// <summary>ACC off.</summary>
    public const string IgnitionOff = "off";

    public static IEndpointRouteBuilder MapInternalSessionEndpoints(
        this IEndpointRouteBuilder endpoints, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        // AllowAnonymous because the caller is a service, not a user: there is no bearer to present
        // and the kernel's fallback policy would otherwise 401 every call. The filter authenticates.
        var internalSessions = endpoints.MapGroup("/v1/internal/sessions")
            .WithTags("sessions")
            .AllowAnonymous()
            .AddEndpointFilter(new TripStateInternalApiKeyFilter(apiKey));

        internalSessions.MapPost("/{sessionId}/auto-end", AutoEndAsync).WithName("autoEndSession");

        // ⚠ Not in D3' — a C031 micro-change-set, added to trip-state.yaml in the same change.
        // D6' §I-25.3 routes "ACC-on/off ingest events (Epic 3 ingest → trip-state-svc)" and AL-32
        // makes them auto-start and auto-end the session, and no endpoint carried them. The tracker
        // plane decodes ACC out of a GT06/JT808 frame (C043) and has nowhere to say so.
        internalSessions.MapPost("/ignition", IgnitionAsync).WithName("reportIgnition");

        return endpoints;
    }

    private static async Task<Ok<SessionResponse>> AutoEndAsync(
        string sessionId,
        AutoEndBody? body,
        ISessionService service,
        IOptions<TripStateOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(options);

        var session = await service.AutoEndAsync(sessionId, body?.Reason, cancellationToken);

        return TypedResults.Ok(SessionResponse.From(session, options.Value.RestartGrace));
    }

    /// <summary>
    /// ACC on/off from a paired tracker (US-3.22/3.23).
    /// </summary>
    /// <remarks>
    /// 202, not 201 or 200: an ignition report is a fact about a device, and whether it opens a
    /// session, closes one or does nothing is this service's decision — the adapter reporting it
    /// has no use for the answer and must not treat "declined" as a failure to retry.
    /// </remarks>
    private static async Task<Accepted<IgnitionResponse>> IgnitionAsync(
        IgnitionBody? body, ISessionService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);

        var on = body?.State switch
        {
            IgnitionOn => true,
            IgnitionOff => false,
            _ => throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["state"] = ["state must be 'on' or 'off'."],
            }),
        };

        var outcome = await service.IgnitionAsync(
            new IgnitionCommand(body?.VehicleId, on, body?.At), cancellationToken);

        return TypedResults.Accepted(
            (string?)null, new IgnitionResponse(outcome.ToString().ToLowerInvariant()));
    }
}

/// <summary>What an ignition report did — <c>started</c>, <c>ended</c>, <c>nochange</c>, <c>declined</c>.</summary>
internal sealed record IgnitionResponse(string Outcome);

/// <summary>Refuses a request that does not carry the internal shared secret.</summary>
/// <remarks>
/// Fixed-time comparison: the header is a secret, and an early-exit <c>string ==</c> leaks its
/// prefix to anybody willing to time a few thousand requests. Same shape as registry-svc's and
/// provisioning-svc's filters.
/// </remarks>
internal sealed class TripStateInternalApiKeyFilter(string apiKey) : IEndpointFilter
{
    private readonly byte[] _expected = Encoding.UTF8.GetBytes(apiKey);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var presented = context.HttpContext.Request.Headers[InternalSessionEndpoints.ApiKeyHeader].ToString();

        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _expected))
        {
            throw new MageRideException(
                MageRideErrors.Unauthorized, "This route is service-to-service only (D3' §0).");
        }

        return await next(context);
    }
}
