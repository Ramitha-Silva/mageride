using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;

namespace MageRide.ApiGateway.Versioning;

/// <summary>
/// What the floor says about one client build (D-31). <paramref name="UpdateRequired"/> is the
/// soft answer the app polls for; <paramref name="IsMandatory"/> is the hard floor the edge gate
/// enforces with <c>426</c>.
/// </summary>
public readonly record struct VersionVerdict(
    bool UpdateRequired, bool IsMandatory, string LatestVersion, string UpdateUrl);

/// <summary>
/// Resolves the configured floor for a platform and compares a client version against it.
/// <para>
/// Shared by the <see cref="AppVersionGateMiddleware"/> (which turns a mandatory verdict into
/// <c>426</c>) and the <c>GET /v1/version/check</c> endpoint (which reports the same verdict
/// without blocking), so the transparent gate and the explicit poll can never disagree.
/// </para>
/// </summary>
public sealed class VersionFloorService(IOptionsMonitor<VersionGateOptions> options)
{
    private readonly IOptionsMonitor<VersionGateOptions> _options =
        options ?? throw new ArgumentNullException(nameof(options));

    public VersionGateOptions Current => _options.CurrentValue;

    public bool TryGetFloor(string? platform, [NotNullWhen(true)] out PlatformVersionFloor? floor)
    {
        floor = null;
        return !string.IsNullOrWhiteSpace(platform)
            && _options.CurrentValue.Platforms.TryGetValue(platform, out floor);
    }

    /// <summary>
    /// Evaluates <paramref name="current"/> against <paramref name="floor"/>. An unparsable
    /// <paramref name="current"/> is treated as below the hard floor: a build whose version string
    /// the platform cannot read is not a build the platform supports.
    /// </summary>
    public static VersionVerdict Evaluate(PlatformVersionFloor floor, string? current)
    {
        ArgumentNullException.ThrowIfNull(floor);

        var minimum = ParseOrZero(floor.MinimumVersion);
        var latest = ParseOrZero(floor.LatestVersion);
        var recommended = ClientVersion.TryParse(floor.RecommendedVersion, out var parsedRecommended)
            ? parsedRecommended
            : latest;

        if (!ClientVersion.TryParse(current, out var client))
        {
            return new VersionVerdict(true, true, floor.LatestVersion, floor.UpdateUrl);
        }

        var mandatory = client < minimum;
        var updateRequired = mandatory || client < recommended;

        return new VersionVerdict(updateRequired, mandatory, floor.LatestVersion, floor.UpdateUrl);
    }

    private static ClientVersion ParseOrZero(string? value) =>
        ClientVersion.TryParse(value, out var parsed) ? parsed : ClientVersion.Zero;
}
