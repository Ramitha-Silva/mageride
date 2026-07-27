using Microsoft.AspNetCore.Http;
using Yarp.ReverseProxy.Model;

namespace MageRide.ApiGateway.Http;

/// <summary>
/// Reads a matched YARP route's <c>Metadata</c> dictionary. Only meaningful inside the proxy
/// pipeline, where the route has already been selected.
/// </summary>
internal static class RouteMetadata
{
    public static string? Get(HttpContext context, string key)
    {
        ArgumentNullException.ThrowIfNull(context);

        var metadata = context.Features.Get<IReverseProxyFeature>()?.Route.Config.Metadata;
        return metadata is not null && metadata.TryGetValue(key, out var value) ? value : null;
    }

    public static bool Is(HttpContext context, string key, string value) =>
        string.Equals(Get(context, key), value, StringComparison.OrdinalIgnoreCase);

    public static string? ClusterId(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.Get<IReverseProxyFeature>()?.Route.Config.ClusterId;
    }

    public static string? RouteId(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.Get<IReverseProxyFeature>()?.Route.Config.RouteId;
    }
}
