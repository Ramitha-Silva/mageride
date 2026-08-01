using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MageRide.Safety.Configuration;
using MageRide.Safety.Persistence;
using MageRide.Safety.Reports;
using MageRide.Safety.Sharing;
using MageRide.Safety.Sos;
using MageRide.Shared.Errors;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace MageRide.Safety.Endpoints;

/// <summary>
/// <c>/v1/internal/safety/**</c> — the seams admin-bff and ride-svc need. **Δ C052.**
/// </summary>
/// <remarks>
/// <para>
/// <b>The moderation pair.</b> <c>admin-bff.yaml</c> declares
/// <c>GET /v1/admin/reports/queue</c> and <c>POST /v1/admin/reports/{reportId}/resolve</c> as
/// admin-bff routes, and <c>reputation.v1.proto</c> says "safety-svc owns the confirmation decision
/// and <c>safety.vehicle_reports</c>". Both are satisfiable at once only if admin-bff is the
/// RBAC-gated, audited front door and the decision itself is made here — so these are the two
/// operations it forwards. Without them the third confirmation has no way to happen and US-12.6's
/// auto-delisting is unreachable.
/// </para>
/// <para>
/// <b>The trip-end hook.</b> D-34's window is "trip end + 1 h" and nothing in this service knows
/// when a trip ends; ride-svc and trip-state-svc do. A sweep over expiry would eventually close a
/// link, but "eventually" is up to <c>Safety:ShareMaxLifetime</c> after a ride that ended in ten
/// minutes.
/// </para>
/// <para>
/// <b>The P-12 read.</b> The rows are ride-svc's to write; the question they exist to answer — "this
/// booker keeps pinging somebody who keeps declining" — had nowhere to be asked.
/// </para>
/// </remarks>
public static class InternalSafetyEndpoints
{
    /// <summary>The guard header, matching every other internal plane on the platform (C008).</summary>
    public const string ApiKeyHeader = "X-MageRide-Internal-Key";

    public static IEndpointRouteBuilder MapInternalSafetyEndpoints(
        this IEndpointRouteBuilder endpoints, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var internalSafety = endpoints.MapGroup("/v1/internal/safety")
            .WithTags("safety-internal")
            .AllowAnonymous()
            .AddEndpointFilter(new InternalKeyFilter(apiKey));

        internalSafety.MapGet("/reports/queue", QueueAsync).WithName("listReportQueueInternal");
        internalSafety.MapPost("/reports/{reportId:guid}/resolve", ResolveAsync).WithName("resolveReportInternal");

        internalSafety.MapPost("/trips/{tripId:guid}/close", CloseTripAsync).WithName("closeTripShares");

        // Δ C066 — AL-44/US-25.5's web SOS. The C052 handoff left this named rather than stubbed
        // ("public-bff is the caller that does not exist yet"); it exists now.
        internalSafety.MapPost("/sos/web", RaiseWebSosAsync).WithName("raiseWebSos");

        internalSafety.MapGet("/location-requests/{bookerId:guid}", AuditAsync)
            .WithName("listLocationRequestAudit");

        return endpoints;
    }

    private static async Task<Ok<CursorPageResponse<VehicleReportResponse>>> QueueAsync(
        string? cursor,
        int? limit,
        IReportService reports,
        IOptions<SafetyOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(options);

        var page = Math.Clamp(limit ?? 20, 1, options.Value.MaxPageSize);
        var pending = await reports.QueueAsync(ParseCursor(cursor), page, cancellationToken);

        var items = pending
            .Select(static report => new VehicleReportResponse(
                report.Id, report.VehicleId, report.Reason, report.RideId, report.Status, report.CreatedAt))
            .ToArray();

        var next = items.Length == page
            ? pending[^1].CreatedAt.ToString("O", CultureInfo.InvariantCulture)
            : null;

        return TypedResults.Ok(new CursorPageResponse<VehicleReportResponse>(items, next));
    }

    private static async Task<Ok<ResolveReportResponse>> ResolveAsync(
        Guid reportId,
        ResolveReportBody? body,
        IReportService reports,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reports);

        // The deciding admin travels on the body rather than on a bearer: the caller is admin-bff,
        // which has already authenticated and RBAC-gated the human and writes the D-35 audit row.
        // Recording *who* decided is what makes a delisting appealable.
        Guid? resolvedBy = Guid.TryParse(body?.ResolvedBy, out var actor) ? actor : null;

        var resolved = await reports.ResolveAsync(
            reportId, body?.Decision?.Trim().ToUpperInvariant() ?? string.Empty, resolvedBy, body?.Note?.Trim(),
            cancellationToken);

        return TypedResults.Ok(new ResolveReportResponse(
            resolved.Report.Id, resolved.Report.Status, resolved.ConfirmedTotal, resolved.Delisted));
    }

    /// <summary>
    /// <c>POST /v1/internal/safety/sos/web</c> — the one route by which a token-only caller writes a
    /// safety event.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Here rather than in public-bff, because <c>safety.sos_events</c> has one writer.</b> The
    /// row, the <c>sos.raised</c> event that puts it on the admin live feed and D-33's dual-gateway
    /// dispatch are one transaction and one SLO; a second component writing that table would put
    /// the five-second budget and the "record, announce, dispatch" ordering in two places and give
    /// the operator's console two sources for the same alert.
    /// </para>
    /// <para>
    /// <b>The token travels and the booker's number does not.</b> public-bff sends a share token and
    /// two coordinates and is told an id and an outcome. Resolving the recipient here is what keeps
    /// D6' I-29.4's "the booker's registered mobile" out of a passenger-facing process altogether,
    /// which is P-02/P-09's fence held by where the column is read.
    /// </para>
    /// </remarks>
    private static async Task<Accepted<RaiseSosResponse>> RaiseWebSosAsync(
        WebSosBody? body,
        ISosService sos,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sos);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(body?.ShareToken))
        {
            errors["shareToken"] = ["shareToken is the credential for a web SOS and is required."];
        }

        if (body?.Lat is not { } lat || lat is < -90 or > 90)
        {
            errors["lat"] = ["lat must be a latitude between -90 and 90."];
        }

        if (body?.Lng is not { } lng || lng is < -180 or > 180)
        {
            errors["lng"] = ["lng must be a longitude between -180 and 180."];
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }

        var raised = await sos.RaiseWebAsync(
            new RaiseWebSosCommand(body!.ShareToken!, body.Lat!.Value, body.Lng!.Value), cancellationToken);

        return TypedResults.Accepted(
            (string?)null,
            new RaiseSosResponse(raised.Event.Id, raised.Event.DispatchedAt, raised.Event.SmsStatus ?? string.Empty));
    }

    private static async Task<Ok<CloseTripSharesResponse>> CloseTripAsync(
        Guid tripId,
        ITripShareService shares,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shares);

        var revoked = await shares.CloseTripAsync(tripId, cancellationToken);

        return TypedResults.Ok(new CloseTripSharesResponse(tripId, revoked));
    }

    private static async Task<Ok<LocationRequestAuditPage>> AuditAsync(
        Guid bookerId,
        int? hours,
        int? limit,
        ILocationRequestAuditRepository audit,
        IOptions<SafetyOptions> options,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        var window = TimeSpan.FromHours(Math.Clamp(hours ?? 24, 1, 24 * 90));
        var since = clock.GetUtcNow() - window;
        var page = Math.Clamp(limit ?? 50, 1, options.Value.MaxPageSize);

        var rows = await audit.ListForBookerAsync(bookerId, since, page, cancellationToken);
        var totals = await audit.SummariseForBookerAsync(bookerId, since, cancellationToken);

        return TypedResults.Ok(new LocationRequestAuditPage(
            bookerId,
            totals,
            [.. rows.Select(static row => new LocationRequestAuditResponse(
                row.RequestId, row.Decision, row.Ts, Fingerprint(row.RiderPhoneHash)))]));
    }

    /// <summary>
    /// A short, stable handle for the subject of a request.
    /// </summary>
    /// <remarks>
    /// The stored value is already a keyed digest of the MSISDN (P-03) and is not reversible; this
    /// shortens it to something a reader can compare across rows — "the same number keeps declining"
    /// — without putting 32 bytes of hex in every row of an admin screen.
    /// </remarks>
    private static string Fingerprint(byte[] hash) =>
        hash.Length == 0
            ? "unknown"
            : Convert.ToHexStringLower(SHA256.HashData(hash))[..12];

    private static DateTimeOffset? ParseCursor(string? cursor) =>
        DateTimeOffset.TryParse(
            cursor, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
}

/// <summary>
/// Rejects a call that does not carry <c>Safety:InternalApiKey</c>.
/// </summary>
/// <remarks>
/// Answers <c>404 not-found</c>, matching what the gateway returns for the <c>/v1/internal</c>
/// prefix (C008). The comparison is fixed-time — a length-varying compare leaks the key a character
/// at a time.
/// </remarks>
internal sealed class InternalKeyFilter(string apiKey) : IEndpointFilter
{
    private readonly byte[] _expected = Encoding.UTF8.GetBytes(
        apiKey ?? throw new ArgumentNullException(nameof(apiKey)));

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var presented = context.HttpContext.Request.Headers[InternalSafetyEndpoints.ApiKeyHeader].ToString();

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _expected)
            ? next(context)
            : throw new MageRideException(MageRideErrors.NotFound, "No such resource.");
    }
}
