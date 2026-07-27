using System.ComponentModel.DataAnnotations;

namespace MageRide.ApiGateway.Attestation;

/// <summary>How hard the edge enforces D-30.</summary>
public enum AttestationMode
{
    /// <summary>Skip the check entirely. The Docker-Compose dev stack and the synthetic-data replica.</summary>
    Disabled = 0,

    /// <summary>Run the check, log the verdict, forward the request anyway. For staged rollout.</summary>
    Audit = 1,

    /// <summary>Run the check and reject a failure with <c>401 attestation-failed</c>. Production.</summary>
    Enforce = 2,
}

/// <summary>
/// D-30 attestation enforcement (D3' §0 "Attestation"; ADD §12.6). Bound from
/// <c>Gateway:Attestation</c>.
/// </summary>
public sealed class AttestationOptions
{
    public const string SectionName = "Gateway:Attestation";

    public AttestationMode Mode { get; set; } = AttestationMode.Enforce;

    /// <summary>
    /// Per-platform override of <see cref="Mode"/>, keyed <c>android</c> / <c>ios</c>. Exists so a
    /// platform can be rolled out behind the other — Android ships in Wave 4a and iOS in 4b, and a
    /// single global switch would mean either enforcing against an app that does not exist yet or
    /// leaving D-30 off for the platform that does.
    /// </summary>
    public IDictionary<string, AttestationMode> PlatformModes { get; init; } =
        new Dictionary<string, AttestationMode>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ceiling on the <c>X-Attestation</c> header, matching <c>_shared.yaml</c>'s <c>maxLength: 8192</c>.</summary>
    [Range(64, 65536)]
    public int MaxTokenLength { get; set; } = 8192;

    /// <summary>
    /// The operations that require attestation — exactly the ones D3' §0 names sensitive (auth,
    /// payments, ride accept, wallet, SOS), which is the set carrying <c>X-Attestation</c> in
    /// <c>backend/contracts/*.yaml</c>. Configured rather than compiled in: adding a sensitive
    /// endpoint must not need a gateway release.
    /// </summary>
    public IList<SensitiveOperation> SensitiveOperations { get; init; } = [];

    public PlayIntegrityOptions PlayIntegrity { get; init; } = new();

    public AppAttestOptions AppAttest { get; init; } = new();
}

/// <summary>One attestation-protected operation: an HTTP method set plus a route template.</summary>
public sealed class SensitiveOperation
{
    /// <summary>e.g. <c>["POST"]</c>. Empty means every method on the path.</summary>
    public IList<string> Methods { get; init; } = [];

    /// <summary>
    /// ASP.NET route template, identical to the OpenAPI path — <c>/v1/rides/{rideId}/offer/{driverId}/accept</c>.
    /// </summary>
    [Required]
    public string Path { get; set; } = string.Empty;
}
