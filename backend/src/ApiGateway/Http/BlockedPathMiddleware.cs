using MageRide.ApiGateway.Configuration;
using MageRide.Shared.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.ApiGateway.Http;

/// <summary>
/// Refuses the path prefixes that must never be reachable from the public edge — today the
/// <c>/v1/internal/**</c> family, which D3' §0 puts on service-to-service mTLS
/// (Linkerd/SPIFFE) and which appears in the OpenAPI contracts only so the calling service knows
/// its shape.
/// </summary>
/// <remarks>
/// Enforced ahead of routing rather than as a YARP route, so a future route table that grows a
/// <c>/v1/**</c> catch-all cannot accidentally expose them, and so nothing has to remember to
/// exclude them again.
/// </remarks>
internal sealed class BlockedPathMiddleware(
    RequestDelegate next,
    IOptionsMonitor<GatewayOptions> options,
    ILogger<BlockedPathMiddleware> logger)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly IOptionsMonitor<GatewayOptions> _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<BlockedPathMiddleware> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var prefix in _options.CurrentValue.BlockedPathPrefixes)
        {
            if (!context.Request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _logger.LogWarning(
                "Refused {Method} {Path} at the edge: {Prefix} is internal-only (mTLS, D3' §0).",
                context.Request.Method, context.Request.Path, prefix);

            return GatewayProblem.WriteAsync(context, MageRideErrors.NotFound, "No such resource.");
        }

        return _next(context);
    }
}
