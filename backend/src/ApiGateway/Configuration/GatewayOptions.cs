using System.ComponentModel.DataAnnotations;

namespace MageRide.ApiGateway.Configuration;

/// <summary>Backing store for the edge state that must be shared across gateway replicas.</summary>
public enum GatewayStateStore
{
    /// <summary>Process-local. Single-instance only.</summary>
    Memory = 0,

    /// <summary>Redis (ADD §9.4). The deployed shape.</summary>
    Redis = 1,
}

/// <summary>
/// Edge-wide settings for the gateway process (D6' §8.2). Everything that is per-route lives on
/// the route's <c>Metadata</c> in <c>gateway-routes.json</c>; everything platform-wide lives here.
/// </summary>
public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    /// <summary>
    /// Route-metadata keys the gateway reads. Declared as constants so a typo in
    /// <c>gateway-routes.json</c> fails a test rather than silently disabling a control.
    /// </summary>
    public static class MetadataKeys
    {
        /// <summary>Token-bucket policy name; absent means <see cref="GatewayRateLimitOptions.DefaultPolicyName"/>.</summary>
        public const string RateLimit = "RateLimit";

        /// <summary><c>exempt</c> takes the route out of the D-31 min-version gate.</summary>
        public const string VersionGate = "VersionGate";

        /// <summary><c>true</c> marks a long-lived streaming route (SignalR): no buffering, long idle timeout.</summary>
        public const string Streaming = "Streaming";
    }

    /// <summary>Metadata value that exempts a route from the version gate.</summary>
    public const string ExemptValue = "exempt";

    /// <summary>
    /// Path prefixes the edge refuses outright, answered <c>404 not-found</c> before routing.
    /// <para>
    /// D3' §0 puts service-to-service calls on mTLS behind <c>/internal</c>; those operations are
    /// in the OpenAPI contracts because a service must know their shape, not because the public
    /// edge should reach them. 404 rather than 403: an unroutable path on the public gateway does
    /// not exist, and confirming that it does would map the internal surface for free.
    /// </para>
    /// </summary>
    public IList<string> BlockedPathPrefixes { get; init; } = ["/v1/internal"];

    /// <summary>
    /// Where the edge keeps the state it shares between replicas: the rate-limit buckets and the
    /// App Attest replay counters. <see cref="GatewayStateStore.Memory"/> is per-process and only
    /// correct for a single gateway instance — the dev compose stack and the test host.
    /// </summary>
    public GatewayStateStore StateStore { get; set; } = GatewayStateStore.Redis;

    /// <summary>
    /// Emit <c>X-MageRide-Upstream: {clusterId}</c> on proxied responses. Off by default — the
    /// cluster map is internal topology. The route-table test turns it on to assert which service
    /// each contract path resolves to.
    /// </summary>
    public bool EmitUpstreamHeader { get; set; }

    /// <summary>
    /// Overwrite the outbound <c>traceparent</c> with this gateway's span, so a backend trace
    /// parents to the gateway rather than to whatever the client sent. Off means the inbound value
    /// is forwarded verbatim.
    /// </summary>
    public bool RewriteTraceParent { get; set; } = true;
}
