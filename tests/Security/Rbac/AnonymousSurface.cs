namespace MageRide.Security.Tests.Rbac;

/// <summary>What authenticates a caller on an endpoint that carries <c>AllowAnonymous</c>.</summary>
internal enum AnonymousCredential
{
    /// <summary>Nothing, and correctly nothing: the response is the same for every caller.</summary>
    None,

    /// <summary>The shared internal key header, compared in fixed time (C042 replaces it with mesh identity).</summary>
    InternalKey,

    /// <summary>An HMAC the payment provider computed over the body.</summary>
    WebhookSignature,

    /// <summary>An HMAC in the query string — the stand-in for a presigned object-storage URL.</summary>
    SignedLink,

    /// <summary>A `safety.trip_share_tokens` row: the token in the path is the whole credential (D-34, AL-44).</summary>
    ShareToken,

    /// <summary>A credential is being established, so there cannot be one yet — OTP, password, refresh.</summary>
    PreCredential,

    /// <summary>A gRPC plane whose caller is a service, authenticated by a server interceptor.</summary>
    GrpcInterceptor,
}

/// <summary>One reviewed anonymous endpoint.</summary>
/// <param name="Key">
/// <c>{service} {VERB} {template}</c>, exactly as <see cref="GuardedEndpoint.Key"/> spells it.
/// </param>
/// <param name="Credential">What the caller must present instead of a bearer.</param>
/// <param name="EdgeReachable">
/// Whether the C008 gateway routes it from the public internet. <c>false</c> means the only way to
/// it is the internal network — a second, independent control, and the reason a shared secret is
/// tolerable on the <c>/v1/internal</c> family at all.
/// </param>
/// <param name="Why">The review note. One sentence, in the reviewer's words, not the code's.</param>
internal sealed record AnonymousEndpoint(
    string Key, AnonymousCredential Credential, bool EdgeReachable, string Why);

/// <summary>
/// Every endpoint in the fleet that opts out of authentication, with the credential that replaces
/// it — the C127 review of the platform's anonymous surface, held as data so the suite can fail
/// when it changes.
///
/// <para>
/// <b>Why this is a list and not a rule.</b> An <c>IEndpointFilter</c> is compiled into the request
/// delegate and leaves no endpoint metadata, so no amount of reflection can tell an
/// <c>AllowAnonymous</c> route that is guarded by <c>InternalKeyFilter</c> from one that is guarded
/// by nothing. Anything automatic would therefore have to either trust every anonymous route or
/// fail on all of them. The list is the review: an entry means somebody read the handler and wrote
/// down what a caller has to present.
/// </para>
///
/// <para>
/// <b>It is a ratchet in both directions</b> (the <c>RouteDrift</c> idiom C118 established). A new
/// anonymous endpoint fails <c>RbacProbeTests</c> until it is added here with a reason, and an entry
/// whose route no longer exists fails it too — so the list cannot rot into a permanent exemption
/// for something that was deleted three components ago.
/// </para>
/// </summary>
internal static class AnonymousSurface
{
    /// <summary>
    /// Routes the shared kernel maps anonymously on <b>every</b> service (<c>UseMageRideDefaults</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Operational surface rather than API: the two probes D7' §3 wires the compose health checks
    /// to, and the D7' §12 Prometheus scrape. They take no credential deliberately — a liveness
    /// probe that needed a token could not run before the token issuer was up.
    /// </para>
    /// <para>
    /// <b>The control is that the edge does not publish them</b>, which is the api-gateway
    /// CLAUDE.md operational note in so many words, and which
    /// <c>security/checks/20-edge-exposure.sh</c> asserts against the running replica rather than
    /// against a config file.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlySet<string> KernelOperationalRoutes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "GET /health/live",
            "GET /health/ready",
            "GET /metrics",
        };

    /// <summary>
    /// The service-to-service plane: <c>/v1/internal/**</c> on twenty services.
    /// </summary>
    /// <remarks>
    /// Two controls, and it takes both. The gateway refuses the prefix ahead of routing
    /// (<c>BlockedPathMiddleware</c>, <c>Gateway:BlockedPathPrefixes</c>), so nothing on the public
    /// internet can address them; and each family carries an <c>InternalKeyFilter</c> that answers
    /// <c>404</c> — never <c>401</c> — so a caller inside the network who is not entitled to the
    /// plane cannot map it either. D3' §0 puts these on mesh mTLS; the shared key is the interim
    /// until C042 lands a SPIFFE identity, and every one of them is listed in the remediation
    /// backlog under that item rather than treated as finished.
    /// </remarks>
    public const string InternalPrefix = "/v1/internal/";

    /// <summary>
    /// Every other anonymous endpoint, one entry each.
    /// </summary>
    public static readonly IReadOnlyList<AnonymousEndpoint> Reviewed =
    [
        // -------------------------------------------------------------------------------------
        // iam-svc — the sign-in surface. A credential is what these produce, so a caller cannot
        // present one first. The compensating controls are D-32's Redis token bucket (60 s resend,
        // 5/h, failing CLOSED), the durable failed-attempt lock-out on the password path, and
        // D-30 attestation on the two OTP routes at the edge.
        // -------------------------------------------------------------------------------------
        new("iam POST /v1/auth/otp/request", AnonymousCredential.PreCredential, true,
            "Phone-OTP start. D-32 rate limit + D-30 attestation; answers the same shape for a "
            + "registered and an unregistered number so it is not a registration oracle."),
        new("iam POST /v1/auth/otp/resend", AnonymousCredential.PreCredential, true,
            "Same bucket as /otp/request — the 60 s cooldown is what this route exists to enforce."),
        new("iam POST /v1/auth/otp/verify", AnonymousCredential.PreCredential, true,
            "The OTP is the credential. Hashed at rest under Otp:PepperKey; D-30 attestation at the edge."),
        new("iam POST /v1/auth/password", AnonymousCredential.PreCredential, true,
            "Portal sign-in (AL-07/AL-37). Durable failed-attempt lock-out on iam.user_credentials, "
            + "not a Redis counter, so a cache flush does not reset every internal account at once."),
        new("iam POST /v1/auth/google", AnonymousCredential.PreCredential, true,
            "The Google ID token is the credential; the audience is checked against Oidc:Google:ClientIds, "
            + "which has no default, and binding is on the provider's sub rather than the asserted email."),
        new("iam POST /v1/auth/apple", AnonymousCredential.PreCredential, true,
            "As /v1/auth/google, for the Fleet Portal (AL-07)."),
        new("iam POST /v1/admin/auth/login", AnonymousCredential.PreCredential, true,
            "Admin Portal sign-in, password or Google code. No MFA by AL-37; the compensating controls "
            + "are the lock-out, session binding and the optional Auth:InternalRoleIpAllowList."),
        new("iam POST /v1/auth/refresh", AnonymousCredential.PreCredential, true,
            "The opaque refresh token is the credential (D-29). Single-use: a replay revokes the whole "
            + "rotation family, which RefreshReuseTests drives."),
        new("iam GET /.well-known/jwks.json", AnonymousCredential.None, true,
            "RFC 7517 public key set. Public by construction — every consumer must fetch it before it "
            + "holds any credential at all."),
        new("iam GET /v1/users/lookup", AnonymousCredential.InternalKey, true,
            "P-03 proxy-booking registration oracle, service-to-service. NOT under /v1/internal, so the "
            + "edge routes it — see finding C127-04. Unset Auth:InternalApiKey unmaps it entirely."),

        // -------------------------------------------------------------------------------------
        // content-svc — the pre-account surface. A first-run screen has no token.
        // -------------------------------------------------------------------------------------
        new("content GET /v1/config/cities", AnonymousCredential.None, true,
            "The operating-city list, read by the first-run city screen before any account exists. "
            + "Reference data; identical for every caller."),
        new("content GET /v1/content/onboarding/{}", AnonymousCredential.None, true,
            "The onboarding carousel, above the language picker on the first-run screen."),
        new("content GET /v1/content/templates/{}", AnonymousCredential.InternalKey, true,
            "notification-svc's D-26 render path. NOT under /v1/internal, so the edge routes it, and "
            + "an unset Content:InternalApiKey leaves it open rather than unmapped — finding C127-04."),

        // -------------------------------------------------------------------------------------
        // fare-svc
        // -------------------------------------------------------------------------------------
        new("fare POST /v1/fare/calculate", AnonymousCredential.InternalKey, true,
            "The final fare of a completed ride. D3' §0 puts it on mesh mTLS; it is NOT under "
            + "/v1/internal, so the edge routes it — finding C127-04. Unset key leaves it unmapped."),

        // -------------------------------------------------------------------------------------
        // Payment-provider callbacks. A gateway presents no bearer; the HMAC over the body is the
        // credential, and the provider transaction id is UNIQUE so a replayed callback is a no-op
        // (ADD §12.6 "late payment callback", R-19).
        // -------------------------------------------------------------------------------------
        new("wallet POST /v1/wallet/topup/onepay/webhook", AnonymousCredential.WebhookSignature, true,
            "OnePay settlement callback; HMAC under Onepay:WebhookSecret."),
        new("wallet POST /v1/wallet/topup/lankaqr/confirm", AnonymousCredential.WebhookSignature, true,
            "LankaQR/ComBank IPG confirmation; HMAC under ComBankIpg:WebhookSecret."),
        new("fleet-billing POST /v1/fleet-billing/topup/onepay/webhook", AnonymousCredential.WebhookSignature, true,
            "As the wallet route, for a fleet organisation's account."),
        new("fleet-billing POST /v1/fleet-billing/topup/lankaqr/confirm", AnonymousCredential.WebhookSignature, true,
            "As the wallet route, for a fleet organisation's account."),
        new("subscription POST /v1/mode-b/pay/onepay/webhook", AnonymousCredential.WebhookSignature, true,
            "Mode B subscription settlement; HMAC under Subscription:OnepayWebhookSecret."),
        new("subscription POST /v1/mode-b/pay/lankaqr/confirm", AnonymousCredential.WebhookSignature, true,
            "Mode B subscription confirmation; HMAC under Subscription:LankaQrWebhookSecret."),

        // -------------------------------------------------------------------------------------
        // Signed links. Each is what a 302 points at, and a browser following a redirect does not
        // carry the bearer that authorised it — so the HMAC in the query string is the credential.
        // The shape a presigned object-storage URL has, which is what they stand in for.
        // -------------------------------------------------------------------------------------
        new("fleet GET /v1/fleets/{}/vehicles/bulk/{}/errors.csv", AnonymousCredential.SignedLink, true,
            "Bulk-vehicle-import error report, handed to a browser download by the Fleet Portal."),
        new("provisioning GET /v1/fleets/{}/trackers/bulk/{}/errors.csv", AnonymousCredential.SignedLink, true,
            "Bulk tracker-binding error report, same arrangement."),
        new("subscription GET /v1/mode-b/files/{}/{}", AnonymousCredential.SignedLink, true,
            "Mode B payment slip; HMAC under Subscription:FileLinkSigningKey."),
        new("support GET /v1/support/screenshots/{}", AnonymousCredential.SignedLink, true,
            "Ticket screenshot; HMAC under Support:FileLinkSigningKey."),
        new("transit GET /v1/admin/transit/gtfs/objects/{}", AnonymousCredential.SignedLink, true,
            "The stored GTFS zip the download route 302s to; HMAC under Transit:Gtfs:DownloadSigningKey."),

        // -------------------------------------------------------------------------------------
        // The share-token surfaces (D-34, AL-44). The token IS the credential and is scoped to one
        // trip, expires at trip_end + 1 h, is revocable, is rate-limited per token and per IP, and
        // is metered. ShareTokenTests drives every one of those properties.
        // -------------------------------------------------------------------------------------
        new("safety GET /v1/trip-share/public/{}", AnonymousCredential.ShareToken, true,
            "The D-34 read: scoped to the trip, no historical replay, revocable, 60 req/min."),
        new("public-bff GET /public/track/{}", AnonymousCredential.ShareToken, true,
            "SCR-WT-001/002 snapshot. public-bff registers NO authentication scheme at all, so the "
            + "token is structurally the only credential it can accept (AL-44)."),
        new("public-bff GET /public/track/{}/live", AnonymousCredential.ShareToken, true,
            "SSE over the same ride/geocell channels, scoped by the same token."),
        new("public-bff GET /public/track/{}/receipt", AnonymousCredential.ShareToken, true,
            "Terminal-state receipt (P-10/P-14)."),
        new("public-bff POST /public/track/{}/pickup/confirm", AnonymousCredential.ShareToken, true,
            "SCR-WT-003; needs the pickup_confirm scope on the token, not merely a live token."),
        new("public-bff POST /public/track/{}/pickup/decline", AnonymousCredential.ShareToken, true,
            "As /pickup/confirm."),
        new("public-bff POST /public/track/{}/sos", AnonymousCredential.ShareToken, true,
            "US-25.6 web SOS — dual-gateway SMS to the booker, safety.sos_events.source='web'."),

        // -------------------------------------------------------------------------------------
        // gRPC. Described by backend/contracts/proto/*.proto, not by OpenAPI, and listening on a
        // port of their own that the edge does not front (Query:GrpcListenPort,
        // Reputation:GrpcListenPort). The catch-all is what every Grpc.AspNetCore host maps for an
        // unimplemented method.
        // -------------------------------------------------------------------------------------
        new("query POST /query.v1.Query/GetNearbyVehicles", AnonymousCredential.GrpcInterceptor, false,
            "query-svc read plane; caller is a service, authenticated by the server interceptor."),
        new("query POST /query.v1.Query/GetTripDetail", AnonymousCredential.GrpcInterceptor, false,
            "As GetNearbyVehicles."),
        new("query POST /query.v1.Query/GetDriverEarnings", AnonymousCredential.GrpcInterceptor, false,
            "As GetNearbyVehicles."),
        new("query GET /query.v1.Query/{}", AnonymousCredential.GrpcInterceptor, false,
            "The gRPC host's unimplemented-method catch-all."),
        new("query GET /{}/{}", AnonymousCredential.GrpcInterceptor, false,
            "The gRPC host's unimplemented-service catch-all."),
        new("reputation POST /reputation.v1.Reputation/GetBlockStatus", AnonymousCredential.GrpcInterceptor, false,
            "D-04 block-status plane; caller is dispatch-svc, authenticated by the server interceptor."),
        new("reputation POST /reputation.v1.Reputation/GetDriverLevel", AnonymousCredential.GrpcInterceptor, false,
            "As GetBlockStatus."),
        new("reputation POST /reputation.v1.Reputation/ReportCancellation", AnonymousCredential.GrpcInterceptor, false,
            "As GetBlockStatus."),
        new("reputation POST /reputation.v1.Reputation/ReportNoShow", AnonymousCredential.GrpcInterceptor, false,
            "As GetBlockStatus."),
        new("reputation POST /reputation.v1.Reputation/ReportVehicle", AnonymousCredential.GrpcInterceptor, false,
            "As GetBlockStatus."),
        new("reputation GET /reputation.v1.Reputation/{}", AnonymousCredential.GrpcInterceptor, false,
            "The gRPC host's unimplemented-method catch-all."),
        new("reputation GET /{}/{}", AnonymousCredential.GrpcInterceptor, false,
            "The gRPC host's unimplemented-service catch-all."),
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, AnonymousEndpoint>> Index = new(() =>
        Reviewed.ToDictionary(static entry => entry.Key, StringComparer.Ordinal));

    /// <summary>The review note for an endpoint, or null if nobody has written one.</summary>
    public static AnonymousEndpoint? Find(GuardedEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (KernelOperationalRoutes.Contains(endpoint.Route))
        {
            return new AnonymousEndpoint(
                endpoint.Key, AnonymousCredential.None, EdgeReachable: false,
                "Kernel operational route, mapped on every service. The edge must not publish it.");
        }

        var template = endpoint.Route.Split(' ', 2)[^1];

        if (template.StartsWith(InternalPrefix, StringComparison.Ordinal))
        {
            return new AnonymousEndpoint(
                endpoint.Key, AnonymousCredential.InternalKey, EdgeReachable: false,
                "Service-to-service plane: refused at the edge by Gateway:BlockedPathPrefixes and "
                + "guarded by an internal-key filter that answers 404 (D3' §0, interim for C042).");
        }

        return Index.Value.GetValueOrDefault(endpoint.Key);
    }
}
