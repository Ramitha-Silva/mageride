using System.Net;
using System.Security.Cryptography;
using System.Text;
using MageRide.Provisioning.Credentials;
using MageRide.Provisioning.Domain;
using MageRide.Provisioning.Trackers;
using MageRide.Shared.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Provisioning.Endpoints;

/// <summary>
/// <c>/v1/internal/trackers</c> — the tcp-adapter's and the rotation cron's surface (T-01, T-12).
/// </summary>
/// <remarks>
/// <para>
/// D3' §0 puts the whole <c>/v1/internal/**</c> family on service-to-service mTLS and the API
/// gateway refuses the prefix at the edge (C008). Until a mesh exists (C042) the in-cluster hop is
/// guarded by a shared secret; without <c>Provisioning:InternalApiKey</c> the routes are not mapped
/// at all, so a deployment that forgets it gets 404s. <b>The failure direction matters here:</b>
/// an adapter that cannot reach <c>validate</c> refuses every device, which is the safe way round.
/// </para>
/// <para>
/// <b>The CRL is the exception and is anonymous.</b> A revocation list is signed, public by design
/// (RFC 5280) and fetched by EMQX during a TLS handshake, where there is nowhere to put a header.
/// Requiring a secret would mean either sharing it with the broker or turning
/// <c>enable_crl_check</c> off, and the list reveals nothing a certificate holder does not already
/// have.
/// </para>
/// </remarks>
public static class InternalTrackerEndpoints
{
    /// <summary>Carries <c>Provisioning:InternalApiKey</c>. Replaced by the mTLS peer identity in C042.</summary>
    public const string ApiKeyHeader = "X-MageRide-Internal-Key";

    /// <summary>
    /// Query parameter the adapter passes on <c>validate</c>: the credential the device presented.
    /// </summary>
    /// <remarks>
    /// ⚠ Not in D3', which types <c>validate</c> as taking the IMEI alone. Raised as a C030
    /// micro-change-set, because without it T-08 is unimplementable on the path where clones
    /// actually appear: a cloned tracker never calls <c>bind</c>, it dials the adapter, and an IMEI
    /// on its own cannot distinguish one device presenting twice from two devices presenting once.
    /// Optional, so an adapter that does not send it still resolves normally — it just contributes
    /// no anti-clone evidence.
    /// </remarks>
    public const string CredentialSerialQuery = "credentialSerial";

    public static IEndpointRouteBuilder MapInternalTrackerEndpoints(
        this IEndpointRouteBuilder endpoints, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        // AllowAnonymous because the caller is a service, not a user: there is no bearer to present
        // and the kernel's fallback policy would otherwise 401 every call. The filter authenticates.
        var internalTrackers = endpoints.MapGroup("/v1/internal/trackers")
            .WithTags("trackers")
            .AllowAnonymous()
            .AddEndpointFilter(new ProvisioningInternalApiKeyFilter(apiKey));

        internalTrackers.MapPost("/{imei}/rotate", RotateAsync).WithName("rotateTrackerCredential");
        internalTrackers.MapGet("/{imei}/validate", ValidateAsync).WithName("validateTracker");

        // ⚠ Not in D3' — a C030 micro-change-set, added to provisioning.yaml in the same change.
        // T-08's second half: an adapter that sees one credential on two live sockets is the only
        // component that *can* see it, and it had no way to say so. Without this the anti-clone
        // rule only ever fires at bind, which is the case a determined cloner never goes through.
        internalTrackers.MapPost("/{imei}/quarantine", QuarantineAsync).WithName("quarantineTracker");

        // Two spellings of one list. The DER form is what a certificate's distribution point names
        // and what a TLS stack fetching a CRL mid-handshake expects; the PEM form is the same bytes
        // armoured, for `openssl crl -text` and for an operator with a browser.
        endpoints.MapGet("/v1/internal/trackers/crl.der", GetCrlDerAsync)
            .WithName("getDeviceCrl")
            .WithTags("trackers")
            .AllowAnonymous();

        endpoints.MapGet("/v1/internal/trackers/crl.pem", GetCrlPemAsync)
            .WithName("getDeviceCrlPem")
            .WithTags("trackers")
            .AllowAnonymous();

        return endpoints;
    }

    private static async Task<Ok<CredentialResponse>> RotateAsync(
        string imei, ITrackerService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);

        var credential = await service.RotateAsync(imei, cancellationToken);

        return TypedResults.Ok(CredentialResponse.From(credential));
    }

    private static async Task<Ok<ValidateResponse>> ValidateAsync(
        string imei, HttpContext context, ITrackerService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var serial = context.Request.Query[CredentialSerialQuery].ToString();

        var verdict = await service.ValidateAsync(
            imei,
            string.IsNullOrWhiteSpace(serial) ? null : serial,
            RemoteAddress(context),
            cancellationToken);

        // Always 200. The adapter asks a question and gets an answer; an unknown IMEI is a verdict
        // and not an error, and a 404 here would put "device we have never heard of" and "this
        // service is misrouted" in the same bucket on the adapter's hot path.
        return TypedResults.Ok(ValidateResponse.From(verdict));
    }

    private static async Task<NoContent> QuarantineAsync(
        string imei, QuarantineTrackerBody? body, ITrackerService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);

        // 204 whether or not there was an ACTIVE binding to hold. The adapter reports the same
        // clone on every reconnect and a second report is not an error — the first one already
        // took the device off the air.
        await service.QuarantineAsync(imei, body?.ReportedBy, body?.Detail, cancellationToken);

        return TypedResults.NoContent();
    }

    private static async Task<IResult> GetCrlDerAsync(ICrlService crl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(crl);

        var document = await crl.GetAsync(cancellationToken);

        return Results.File(document.Der, "application/pkix-crl");
    }

    private static async Task<IResult> GetCrlPemAsync(ICrlService crl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(crl);

        var document = await crl.GetAsync(cancellationToken);

        return Results.Text(document.Pem, "application/x-pem-file", Encoding.UTF8);
    }

    /// <summary>
    /// The device's address, as the adapter reports it.
    /// </summary>
    /// <remarks>
    /// The adapter terminates the device's socket and calls this over its own, so
    /// <c>RemoteIpAddress</c> is the adapter. <c>X-Forwarded-For</c>'s first entry is the device
    /// when the adapter passes one; it is evidence in an audit trail rather than an authorisation
    /// input, so taking it on trust costs nothing.
    /// </remarks>
    private static IPAddress? RemoteAddress(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();

        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var first = forwarded.Split(',')[0].Trim();

            if (IPAddress.TryParse(first, out var parsed))
            {
                return parsed;
            }
        }

        return context.Connection.RemoteIpAddress;
    }
}

/// <summary>Refuses a request that does not carry the internal shared secret.</summary>
/// <remarks>
/// Fixed-time comparison: the header is a secret, and an early-exit <c>string ==</c> leaks its
/// prefix to anybody willing to time a few thousand requests. Same shape as registry-svc's filter.
/// </remarks>
internal sealed class ProvisioningInternalApiKeyFilter(string apiKey) : IEndpointFilter
{
    private readonly byte[] _expected = Encoding.UTF8.GetBytes(apiKey);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var presented = context.HttpContext.Request.Headers[InternalTrackerEndpoints.ApiKeyHeader].ToString();

        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _expected))
        {
            throw new MageRideException(
                MageRideErrors.Unauthorized, "This route is service-to-service only (D3' §0).");
        }

        return await next(context);
    }
}
