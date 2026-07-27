using MageRide.ApiGateway.Configuration;
using MageRide.ApiGateway.Http;
using MageRide.Shared.Errors;
using MageRide.Shared.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MageRide.ApiGateway.Versioning;

/// <summary>
/// D-31: reads <c>X-App-Version</c> and <c>X-Platform</c> off every proxied request and answers
/// <c>426 Upgrade Required</c> when the caller is below the platform's hard floor. The body is the
/// platform's RFC 7807 problem carrying <c>updateUrl</c>, <c>latestVersion</c> and
/// <c>isMandatory</c> (D3' §0 "Min-version gate").
/// </summary>
/// <remarks>
/// Runs inside the YARP proxy pipeline, so the gateway's own endpoints — <c>/v1/version/check</c>,
/// <c>/health/live</c>, <c>/health/ready</c>, <c>/metrics</c> — are exempt by construction. A
/// client too old to be served must still be able to ask what to install.
/// </remarks>
internal sealed class AppVersionGateMiddleware(
    RequestDelegate next,
    VersionFloorService floors,
    ILogger<AppVersionGateMiddleware> logger)
{
    /// <summary>Extension members D3' §0 names for the 426 body.</summary>
    internal const string UpdateUrlExtension = "updateUrl";
    internal const string LatestVersionExtension = "latestVersion";
    internal const string IsMandatoryExtension = "isMandatory";

    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly VersionFloorService _floors = floors ?? throw new ArgumentNullException(nameof(floors));
    private readonly ILogger<AppVersionGateMiddleware> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = _floors.Current;

        if (!options.Enabled || RouteMetadata.Is(context, GatewayOptions.MetadataKeys.VersionGate, GatewayOptions.ExemptValue))
        {
            return _next(context);
        }

        if (!context.Request.TryGetClientPlatform(out var platform) || !_floors.TryGetFloor(platform, out var floor))
        {
            // Not one of our apps (a portal, a public track page, a health prober) or a platform
            // with no configured floor. Nothing to compare against.
            return options.RequirePlatformHeader
                ? Reject(context, platform, null, null)
                : _next(context);
        }

        var current = context.Request.Headers[MageRideHeaders.AppVersion].ToString();
        var verdict = VersionFloorService.Evaluate(floor, current);

        return verdict.IsMandatory
            ? Reject(context, platform, current, verdict)
            : _next(context);
    }

    private Task Reject(HttpContext context, string? platform, string? current, VersionVerdict? verdict)
    {
        _logger.LogInformation(
            "426 upgrade-required at the edge: platform={Platform} version={Version} path={Path}",
            platform ?? "(none)", string.IsNullOrEmpty(current) ? "(none)" : current, context.Request.Path);

        var extensions = verdict is { } v
            ? new KeyValuePair<string, object?>[]
            {
                new(UpdateUrlExtension, v.UpdateUrl),
                new(LatestVersionExtension, v.LatestVersion),
                new(IsMandatoryExtension, v.IsMandatory),
            }
            :
            [
                // RequirePlatformHeader rejected the request before a platform was known, so there
                // is no store link to hand back. The three members stay present with null/false so
                // a client deserialiser sees the same shape either way.
                new(UpdateUrlExtension, null),
                new(LatestVersionExtension, null),
                new(IsMandatoryExtension, true),
            ];

        return GatewayProblem.WriteAsync(
            context,
            MageRideErrors.UpgradeRequired,
            verdict is null
                ? "This client did not identify its platform; send X-Platform and X-App-Version."
                : "This app version is below the minimum supported version.",
            extensions);
    }
}
