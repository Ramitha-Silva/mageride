using System.Diagnostics.Metrics;
using MageRide.Shared.Observability;

namespace MageRide.ApiGateway.Observability;

/// <summary>
/// Edge counters. Created on the shared <see cref="MageRideDiagnostics.Meter"/> so they are picked
/// up by the <c>/metrics</c> scrape D7' §12 already configures, with no second meter to register.
/// </summary>
internal static class GatewayDiagnostics
{
    /// <summary>Requests answered <c>426</c> by the min-version gate, tagged by platform (D-31).</summary>
    public static readonly Counter<long> VersionGateRejections =
        MageRideDiagnostics.Meter.CreateCounter<long>(
            "mageride.gateway.version_gate.rejections", "{request}",
            "Requests rejected at the edge for being below the minimum app version.");

    /// <summary>Requests answered <c>401 attestation-failed</c>, tagged by platform and reason (D-30).</summary>
    public static readonly Counter<long> AttestationRejections =
        MageRideDiagnostics.Meter.CreateCounter<long>(
            "mageride.gateway.attestation.rejections", "{request}",
            "Requests rejected at the edge by attestation enforcement.");

    /// <summary>Attestation failures that were logged but forwarded because the mode is Audit.</summary>
    public static readonly Counter<long> AttestationAudited =
        MageRideDiagnostics.Meter.CreateCounter<long>(
            "mageride.gateway.attestation.audited", "{request}",
            "Attestation failures observed while running in audit mode.");

    /// <summary>Proxy attempts that never produced a backend response, tagged by the YARP error code.</summary>
    public static readonly Counter<long> ForwarderErrors =
        MageRideDiagnostics.Meter.CreateCounter<long>(
            "mageride.gateway.forwarder.errors", "{request}",
            "Proxied requests that failed before or during forwarding.");
}
