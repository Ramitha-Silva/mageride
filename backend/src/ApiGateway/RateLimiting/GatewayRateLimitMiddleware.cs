using System.Globalization;
using MageRide.ApiGateway.Configuration;
using MageRide.ApiGateway.Http;
using MageRide.Shared.Errors;
using MageRide.Shared.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.ApiGateway.RateLimiting;

/// <summary>
/// Per-route edge ceiling (D6' §8.2). The bucket is keyed by policy, matched route and caller, so
/// a client exhausting the booking route still gets its SOS through.
/// </summary>
internal sealed class GatewayRateLimitMiddleware(
    RequestDelegate next,
    ITokenBucketRateLimiter limiter,
    IOptionsMonitor<GatewayRateLimitOptions> options,
    ILogger<GatewayRateLimitMiddleware> logger)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly ITokenBucketRateLimiter _limiter = limiter ?? throw new ArgumentNullException(nameof(limiter));
    private readonly IOptionsMonitor<GatewayRateLimitOptions> _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<GatewayRateLimitMiddleware> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var settings = _options.CurrentValue;
        if (!settings.Enabled)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var policyName = RouteMetadata.Get(context, GatewayOptions.MetadataKeys.RateLimit)
            ?? GatewayRateLimitOptions.DefaultPolicyName;

        var policy = settings.Resolve(policyName);
        if (policy is null)
        {
            // A route naming a policy nobody defined must not silently become unlimited.
            _logger.LogError(
                "Route {Route} names rate-limit policy '{Policy}', which is not configured; applying no limit.",
                RouteMetadata.RouteId(context), policyName);

            await _next(context).ConfigureAwait(false);
            return;
        }

        RateLimitDecision decision;
        try
        {
            decision = await _limiter
                .TryAcquireAsync(policy, Subject(context), 1, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (settings.FailOpen)
        {
            _logger.LogError(ex, "Edge rate limiter unavailable; forwarding without a ceiling (FailOpen).");
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (decision.Allowed)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds));

        await GatewayProblem.WriteAsync(
            context,
            MageRideErrors.RateLimited,
            "Too many requests for this route; retry after the interval in the Retry-After header.",
            configureResponse: response =>
                response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Who the bucket belongs to: the matched route plus the caller's address.
    /// </summary>
    /// <remarks>
    /// Deliberately not the JWT subject. The gateway does not validate tokens (services do, AL-06),
    /// so a <c>sub</c> read here would be unverified and a caller could mint a fresh one per
    /// request to reset its own bucket. The address is the only identity the edge can trust, and it
    /// is only trustworthy at all because <c>UseForwardedHeaders</c> is configured with the known
    /// proxy list — an unlisted hop's <c>X-Forwarded-For</c> is ignored.
    /// </remarks>
    private static string Subject(HttpContext context)
    {
        var route = RouteMetadata.RouteId(context) ?? "unrouted";
        var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return string.Concat(route, "|", address);
    }
}
