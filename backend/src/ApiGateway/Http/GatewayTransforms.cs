using System.Diagnostics;
using MageRide.ApiGateway.Configuration;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace MageRide.ApiGateway.Http;

/// <summary>
/// The transforms every proxied request and response passes through: correlation headers on the
/// way in, an optional upstream marker on the way out.
/// </summary>
internal static class GatewayTransforms
{
    /// <summary>Names the cluster that served a response. Off unless <c>Gateway:EmitUpstreamHeader</c>.</summary>
    public const string UpstreamHeaderName = "X-MageRide-Upstream";

    public static void Configure(TransformBuilderContext context, IOptionsMonitor<GatewayOptions> options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        context.AddRequestTransform(transform =>
        {
            var settings = options.CurrentValue;

            // X-Request-Id was normalised (or minted) on the inbound request by
            // RequestContextMiddleware; YARP copies request headers, so it is already on the
            // outbound message. Setting it from TraceIdentifier keeps the two in step even if a
            // later middleware changes it.
            transform.ProxyRequest.Headers.Remove(RequestContextMiddleware.HeaderName);
            transform.ProxyRequest.Headers.TryAddWithoutValidation(
                RequestContextMiddleware.HeaderName, transform.HttpContext.TraceIdentifier);

            if (settings.RewriteTraceParent && Activity.Current is { } activity)
            {
                // Without this the backend's span parents to whatever traceparent the client sent,
                // and the gateway hop vanishes from the trace. YARP copies the inbound header
                // verbatim, so it has to be replaced rather than added.
                transform.ProxyRequest.Headers.Remove("traceparent");
                transform.ProxyRequest.Headers.TryAddWithoutValidation("traceparent", activity.Id);

                transform.ProxyRequest.Headers.Remove("tracestate");
                if (!string.IsNullOrEmpty(activity.TraceStateString))
                {
                    transform.ProxyRequest.Headers.TryAddWithoutValidation("tracestate", activity.TraceStateString);
                }
            }

            return ValueTask.CompletedTask;
        });

        context.AddResponseTransform(transform =>
        {
            if (options.CurrentValue.EmitUpstreamHeader)
            {
                var cluster = RouteMetadata.ClusterId(transform.HttpContext);
                if (!string.IsNullOrEmpty(cluster))
                {
                    transform.HttpContext.Response.Headers[UpstreamHeaderName] = cluster;
                }
            }

            return ValueTask.CompletedTask;
        });
    }
}
