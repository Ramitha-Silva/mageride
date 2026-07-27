using System.ComponentModel.DataAnnotations;
using MageRide.Shared.Http;

namespace MageRide.ApiGateway.Versioning;

/// <summary>
/// The D-31 minimum-version floor, per platform. Configured under <c>Gateway:VersionGate</c>;
/// an operator raises a floor by changing configuration, never by shipping the gateway.
/// </summary>
public sealed class VersionGateOptions
{
    public const string SectionName = "Gateway:VersionGate";

    /// <summary>Enforce the gate on proxied requests. The <c>/v1/version/check</c> poll answers either way.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Reject a proxied request that carries no recognised <c>X-Platform</c>.
    /// <para>
    /// Off by default: the Admin and Fleet portals are browsers and send neither D-31 header, and
    /// the <c>/public/track</c> pages are opened from an SMS link. Stripping the header is not a
    /// bypass worth closing here — the floor exists for client/server compatibility (US-17.1/17.2),
    /// while the control that stops a tampered client is attestation (D-30), which is enforced
    /// independently and cannot be evaded by omitting a header.
    /// </para>
    /// </summary>
    public bool RequirePlatformHeader { get; set; }

    /// <summary>Floors keyed by <c>android</c> / <c>ios</c> (<see cref="ClientPlatforms"/>).</summary>
    public IDictionary<string, PlatformVersionFloor> Platforms { get; init; } =
        new Dictionary<string, PlatformVersionFloor>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One platform's floor. Versions are <c>major.minor.patch[+build]</c> (<see cref="AppVersion"/>).</summary>
public sealed class PlatformVersionFloor
{
    /// <summary>Hard floor. Below this every request is answered <c>426</c> and the update is mandatory.</summary>
    [Required]
    public string MinimumVersion { get; set; } = "0.0.0";

    /// <summary>
    /// Soft floor. Between <see cref="MinimumVersion"/> and this the client still works, but
    /// <c>/v1/version/check</c> reports <c>updateRequired</c> with <c>isMandatory:false</c> so the
    /// app can show a dismissible prompt. Defaults to <see cref="LatestVersion"/>.
    /// </summary>
    public string? RecommendedVersion { get; set; }

    /// <summary>The current store build.</summary>
    [Required]
    public string LatestVersion { get; set; } = "0.0.0";

    /// <summary>Play Store / App Store deep link for this platform.</summary>
    [Required]
    [Url]
    public string UpdateUrl { get; set; } = string.Empty;
}
