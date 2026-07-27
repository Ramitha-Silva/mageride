using MageRide.ApiGateway.Observability;
using MageRide.Shared.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Forwarder;

namespace MageRide.ApiGateway.Http;

/// <summary>
/// Turns a failed forward into the platform's error shape.
/// </summary>
/// <remarks>
/// YARP answers a destination it could not reach with a bare <c>502</c> and no body. Every other
/// error a client can see is <c>application/problem+json</c> with a registry code (D3' §0), so a
/// naked 502 would be the one response a client cannot parse — and the one most likely to arrive
/// during an incident. Mapped onto the kernel codes D6' §8.3 already names:
/// <c>upstream-timeout</c> (504) for a timeout, <c>dependency-unavailable</c> (503) otherwise.
/// </remarks>
internal sealed class ForwarderErrorMiddleware(RequestDelegate next, ILogger<ForwarderErrorMiddleware> logger)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly ILogger<ForwarderErrorMiddleware> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        await _next(context).ConfigureAwait(false);

        var failure = context.Features.Get<IForwarderErrorFeature>();
        if (failure is null)
        {
            return;
        }

        GatewayDiagnostics.ForwarderErrors.Add(
            1, new KeyValuePair<string, object?>("error", failure.Error.ToString()));

        if (failure.Error is ForwarderError.RequestCanceled or ForwarderError.RequestBodyCanceled
            or ForwarderError.ResponseBodyCanceled or ForwarderError.UpgradeRequestCanceled
            or ForwarderError.UpgradeResponseCanceled)
        {
            // The caller hung up. There is nobody left to hand a problem document to.
            return;
        }

        if (context.Response.HasStarted)
        {
            // A streaming response failed part-way through; the status line is already on the wire.
            _logger.LogWarning(
                "Forwarder error {Error} on {Path} after the response had started.",
                failure.Error, context.Request.Path);
            return;
        }

        var error = failure.Error is ForwarderError.RequestTimedOut or ForwarderError.UpgradeActivityTimeout
            ? MageRideErrors.UpstreamTimeout
            : MageRideErrors.DependencyUnavailable;

        _logger.LogError(
            failure.Exception,
            "Forwarder error {Error} on {Method} {Path}; answering {Status} {Code}.",
            failure.Error, context.Request.Method, context.Request.Path, error.Status, error.Code);

        await GatewayProblem.WriteAsync(
            context, error, "The upstream service could not be reached.").ConfigureAwait(false);
    }
}
