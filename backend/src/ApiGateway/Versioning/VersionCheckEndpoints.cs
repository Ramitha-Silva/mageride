using MageRide.ApiGateway.Http;
using MageRide.Shared.Errors;
using MageRide.Shared.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MageRide.ApiGateway.Versioning;

/// <summary>
/// <c>version-check</c> (D3' "version-check — gateway gate (/v1/version)"). The explicit poll an
/// app makes at cold start, answered by the gateway itself: the floor table already lives here for
/// the transparent D-31 gate, and a client below the floor could not reach a separate service
/// through the very gate that is rejecting it.
/// </summary>
internal static class VersionCheckEndpoints
{
    public const string Path = "/v1/version/check";

    /// <summary>Response shape from <c>version-check.yaml#/paths/~1v1~1version~1check</c>.</summary>
    internal sealed record VersionCheckResponse(
        bool UpdateRequired, string LatestVersion, string UpdateUrl, bool IsMandatory);

    public static IEndpointRouteBuilder MapVersionCheck(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(Path, static (
            HttpContext context,
            VersionFloorService floors,
            string? platform,
            string? current) =>
        {
            var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

            if (string.IsNullOrWhiteSpace(platform))
            {
                errors["platform"] = ["platform is required."];
            }
            else if (!IsKnownPlatform(platform))
            {
                errors["platform"] = ["platform must be 'android' or 'ios'."];
            }

            if (string.IsNullOrWhiteSpace(current))
            {
                errors["current"] = ["current is required."];
            }
            else if (!ClientVersion.TryParse(current, out _))
            {
                errors["current"] = ["current must be a semantic version, e.g. 1.4.0."];
            }

            if (errors.Count > 0)
            {
                return WriteValidationAsync(context, errors);
            }

            if (!floors.TryGetFloor(platform, out var floor))
            {
                // The platform is well-formed but nobody has configured a floor for it. Saying
                // "you are current" is the only answer that does not brick every install of a
                // platform whose configuration was forgotten.
                return Results.Json(
                    new VersionCheckResponse(false, current!, string.Empty, false),
                    MageRideJson.Options).ExecuteAsync(context);
            }

            var verdict = VersionFloorService.Evaluate(floor, current);

            return Results.Json(
                new VersionCheckResponse(
                    verdict.UpdateRequired, verdict.LatestVersion, verdict.UpdateUrl, verdict.IsMandatory),
                MageRideJson.Options).ExecuteAsync(context);
        })
        .AllowAnonymous()
        .WithName("checkAppVersion");

        return endpoints;
    }

    private static bool IsKnownPlatform(string platform) =>
        string.Equals(platform, ClientPlatforms.Android, StringComparison.OrdinalIgnoreCase)
        || string.Equals(platform, ClientPlatforms.Ios, StringComparison.OrdinalIgnoreCase);

    private static Task WriteValidationAsync(HttpContext context, IReadOnlyDictionary<string, string[]> errors) =>
        GatewayProblem.WriteAsync(
            context,
            MageRideErrors.ValidationFailed,
            "One or more query parameters are invalid.",
            [new KeyValuePair<string, object?>(MageRideProblem.ErrorsExtension, errors)]);
}
