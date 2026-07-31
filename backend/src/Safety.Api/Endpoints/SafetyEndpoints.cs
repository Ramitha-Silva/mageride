using System.Globalization;
using MageRide.Safety.Configuration;
using MageRide.Safety.Domain;
using MageRide.Safety.Persistence;
using MageRide.Safety.Reports;
using MageRide.Safety.Sharing;
using MageRide.Safety.Sos;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace MageRide.Safety.Endpoints;

/// <summary>
/// <c>/v1/sos</c>, <c>/v1/trip-share</c>, <c>/v1/reports</c> and <c>/v1/drivers/{id}/block</c>.
/// </summary>
public static class SafetyEndpoints
{
    public static IEndpointRouteBuilder MapSafetyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var sos = endpoints.MapGroup("/v1/sos").WithTags("sos").RequireAuthorization();

        sos.MapPost(string.Empty, RaiseSosAsync).WithName("triggerSos");
        sos.MapGet("/{userId:guid}/history", SosHistoryAsync).WithName("listSosHistory");

        var share = endpoints.MapGroup("/v1/trip-share").WithTags("trip-share");

        share.MapPost("/{tripId:guid}", IssueShareAsync).RequireAuthorization().WithName("createTripShare");
        share.MapDelete("/{tripId:guid}", RevokeShareAsync).RequireAuthorization().WithName("revokeTripShare");

        // **No authentication — the token is the credential** (D-34). Rate-limited per token and per
        // IP inside the service, before the token is even looked up.
        share.MapGet("/public/{token}", ReadShareAsync).AllowAnonymous().WithName("getSharedTrip");

        endpoints.MapPost("/v1/reports/vehicle", ReportVehicleAsync)
            .WithTags("reports")
            .RequireAuthorization()
            .WithName("reportVehicle");

        var block = endpoints.MapGroup("/v1/drivers/{driverId:guid}/block")
            .WithTags("reports")
            .RequireAuthorization();

        block.MapPost(string.Empty, BlockDriverAsync).WithName("blockDriver");
        block.MapDelete(string.Empty, UnblockDriverAsync).WithName("unblockDriver");

        return endpoints;
    }

    // -----------------------------------------------------------------------------------------
    // SOS
    // -----------------------------------------------------------------------------------------

    private static async Task<Ok<RaiseSosResponse>> RaiseSosAsync(
        RaiseSosBody? body,
        HttpContext context,
        ISosService sos,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sos);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (body?.Lat is not { } lat || lat is < -90 or > 90)
        {
            errors["lat"] = ["lat must be a latitude between -90 and 90."];
        }

        if (body?.Lng is not { } lng || lng is < -180 or > 180)
        {
            errors["lng"] = ["lng must be a longitude between -180 and 180."];
        }

        if (!SosRoles.IsKnown(body?.Role))
        {
            errors["role"] = ["role must be 'passenger' or 'driver'."];
        }

        Guid? rideId = null;

        if (!string.IsNullOrWhiteSpace(body?.RideId))
        {
            if (!Guid.TryParse(body.RideId, out var parsed))
            {
                errors["rideId"] = ["rideId is not an id."];
            }
            else
            {
                rideId = parsed;
            }
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors, "The SOS is not valid.");
        }

        var raised = await sos.RaiseAsync(
            new RaiseSosCommand(
                context.User.RequireSubjectId(),
                body!.Role!,
                rideId,
                body.Lat!.Value,
                body.Lng!.Value,
                SosSources.App,
                ShareToken: null),
            cancellationToken);

        return TypedResults.Ok(new RaiseSosResponse(
            raised.Event.Id,
            raised.Event.DispatchedAt,
            raised.Event.SmsStatus ?? SosSmsStatuses.Failed));
    }

    private static async Task<Ok<CursorPageResponse<SosEventResponse>>> SosHistoryAsync(
        Guid userId,
        string? cursor,
        int? limit,
        HttpContext context,
        ISosService sos,
        IOptions<SafetyOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sos);
        ArgumentNullException.ThrowIfNull(options);

        var page = Math.Clamp(limit ?? 20, 1, options.Value.MaxPageSize);

        var events = await sos.HistoryAsync(
            context.User.RequireSubjectId(), userId, ParseCursor(cursor), page, cancellationToken);

        var items = events
            .Select(static e => new SosEventResponse(
                e.Id, e.RideId, e.Role, e.Lat, e.Lng, e.Source, e.SmsStatus, e.AdminAckedAt, e.DispatchedAt, e.Ts))
            .ToArray();

        // The cursor is the last row's instant, not an offset: an SOS raised mid-scroll must not
        // shift the page under the reader.
        var next = items.Length == page
            ? events[^1].Ts.ToString("O", CultureInfo.InvariantCulture)
            : null;

        return TypedResults.Ok(new CursorPageResponse<SosEventResponse>(items, next));
    }

    // -----------------------------------------------------------------------------------------
    // Trip share (D-34)
    // -----------------------------------------------------------------------------------------

    private static async Task<Created<TripShareResponse>> IssueShareAsync(
        Guid tripId,
        HttpContext context,
        ITripShareService shares,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(shares);

        var issued = await shares.IssueAsync(context.User.RequireSubjectId(), tripId, cancellationToken);

        return TypedResults.Created(
            issued.Url, new TripShareResponse(issued.Token, issued.Url, issued.ExpiresAt));
    }

    private static async Task<NoContent> RevokeShareAsync(
        Guid tripId,
        HttpContext context,
        ITripShareService shares,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(shares);

        await shares.RevokeAsync(context.User.RequireSubjectId(), tripId, cancellationToken);

        return TypedResults.NoContent();
    }

    private static async Task<Ok<SharedTripResponse>> ReadShareAsync(
        string token,
        HttpContext context,
        ITripShareService shares,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(shares);

        var view = await shares.ReadAsync(token, ClientIpOf(context), cancellationToken);

        return TypedResults.Ok(new SharedTripResponse(
            view.State,
            view.Position is { } position ? new GeoPointResponse(position.Lat, position.Lng) : null,
            view.Position?.Heading,
            view.VehicleRegistration is null && view.VehicleType is null
                ? null
                : new VehicleResponse(view.VehicleType, view.VehicleRegistration),
            view.DriverName,
            view.AsOf,
            view.ExpiresAt));
    }

    // -----------------------------------------------------------------------------------------
    // Reports and blocks
    // -----------------------------------------------------------------------------------------

    private static async Task<Created<VehicleReportResponse>> ReportVehicleAsync(
        ReportVehicleBody? body,
        HttpContext context,
        IReportService reports,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(reports);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (!Guid.TryParse(body?.VehicleId, out var vehicleId))
        {
            errors["vehicleId"] = ["vehicleId is required."];
        }

        var reason = body?.Reason?.Trim();

        if (string.IsNullOrWhiteSpace(reason))
        {
            errors["reason"] = ["A reason is required."];
        }
        else if (reason.Length > 2_000)
        {
            errors["reason"] = ["A reason is at most 2000 characters."];
        }

        Guid? tripId = null;

        if (!string.IsNullOrWhiteSpace(body?.TripId))
        {
            if (!Guid.TryParse(body.TripId, out var parsed))
            {
                errors["tripId"] = ["tripId is not an id."];
            }
            else
            {
                tripId = parsed;
            }
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors, "The report is not valid.");
        }

        var report = await reports.ReportVehicleAsync(
            context.User.RequireSubjectId(), vehicleId, tripId, reason!, cancellationToken);

        return TypedResults.Created(
            $"/v1/reports/vehicle/{report.Id}",
            new VehicleReportResponse(
                report.Id, report.VehicleId, report.Reason, report.RideId, report.Status, report.CreatedAt));
    }

    private static async Task<NoContent> BlockDriverAsync(
        Guid driverId,
        BlockDriverBody? body,
        HttpContext context,
        IReportService reports,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(reports);

        // `reason` is accepted and not stored: `safety.blocked_drivers` (0903) has no column for
        // one, US-12.10 asks for none, and inventing a column for a free-text field nothing reads
        // would put a passenger's opinion of a named driver in the database for ever. Recorded in
        // the C052 handoff.
        _ = body?.Reason;

        await reports.BlockAsync(context.User.RequireSubjectId(), driverId, cancellationToken);

        return TypedResults.NoContent();
    }

    private static async Task<NoContent> UnblockDriverAsync(
        Guid driverId,
        HttpContext context,
        IReportService reports,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(reports);

        await reports.UnblockAsync(context.User.RequireSubjectId(), driverId, cancellationToken);

        return TypedResults.NoContent();
    }

    // -----------------------------------------------------------------------------------------

    private static DateTimeOffset? ParseCursor(string? cursor) =>
        DateTimeOffset.TryParse(
            cursor, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// The caller's address for the per-IP limit.
    /// </summary>
    /// <remarks>
    /// Every request arrives through the C008 gateway, which sets <c>X-Forwarded-For</c>; the first
    /// entry is the original client. Falls back to the socket address, which in a deployment with no
    /// proxy is the same thing.
    /// </remarks>
    private static string? ClientIpOf(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();

        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var first = forwarded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (first.Length > 0)
            {
                return first[0];
            }
        }

        return context.Connection.RemoteIpAddress?.ToString();
    }
}
