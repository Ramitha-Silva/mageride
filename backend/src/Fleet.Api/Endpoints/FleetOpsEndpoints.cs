using System.Text;
using MageRide.Fleet.Authorization;
using MageRide.Fleet.Bulk;
using MageRide.Fleet.Configuration;
using MageRide.Fleet.Domain;
using MageRide.Fleet.Operations;
using MageRide.Fleet.Subscriptions;
using MageRide.Fleet.Vehicles;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace MageRide.Fleet.Endpoints;

/// <summary>
/// C059's share of <c>/v1/fleets/{fleetId}</c>: vehicle onboarding, AL-50's document slots, driver
/// assignment, tracker binding, scheduling, the live map, analytics and geofences.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every route is mapped on a group C058 built, and that is load-bearing.</b>
/// <c>FleetEndpoints.FleetVehiclesGroup</c> and <c>FleetAssignmentsGroup</c> already carry
/// <c>AddEndpointFilter&lt;FleetAccessFilter&gt;()</c> and <c>RequireApprovedFleet()</c>, so
/// "an unapproved org receives 403 on every vehicle and assignment endpoint" is true of these
/// routes because of where they were mapped rather than because each handler remembered — and
/// <c>Every_vehicle_and_assignment_route_is_gated</c> walks the endpoint data source to keep it
/// true for whoever comes next. The ops group below is built the same way.
/// </para>
/// <para>
/// <b>Reading is never gated on approval; onboarding is.</b> US-13.A7 disables "onboarding and
/// assignment", not monitoring — so the map, the analytics and the alerts sit outside the gate and
/// a PENDING organisation can watch the vehicles it already had. Every mutation is inside it.
/// </para>
/// <para>
/// <b>Every route declares its minimum sub-role.</b> Manager for the operational writes (US-13.A5:
/// "Manager = onboarding/assignment/scheduling/monitoring"), Viewer for the reads, and Owner for
/// the two proxies that decide money — the fare override and the cash/slip confirmation, which
/// US-23.6 puts in the Owner's hands alone.
/// </para>
/// </remarks>
public static class FleetOpsEndpoints
{
    /// <summary>Prefix the operational routes live under. Not approval-gated — see the remarks.</summary>
    public const string FleetOpsGroup = "/v1/fleets/{fleetId}";

    public static IEndpointRouteBuilder MapFleetOpsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var settings = endpoints.ServiceProvider.GetRequiredService<IOptions<FleetOptions>>().Value;

        MapVehicleRoutes(endpoints);
        MapAssignmentRoutes(endpoints);
        MapOperationsRoutes(endpoints, settings);

        if (!string.IsNullOrWhiteSpace(settings.SubscriptionBaseUrl))
        {
            MapSubscriptionProxies(endpoints);
        }

        return endpoints;
    }

    // ---------------------------------------------------------------------------------------
    // Vehicles, documents and the bulk import — on C058's approval-gated group
    // ---------------------------------------------------------------------------------------

    private static void MapVehicleRoutes(IEndpointRouteBuilder endpoints)
    {
        var vehicles = endpoints.MapGroup(FleetEndpoints.FleetVehiclesGroup)
            .WithTags("fleet-vehicles")
            .RequireAuthorization()
            .AddEndpointFilter<FleetAccessFilter>()
            .RequireApprovedFleet();

        vehicles.MapPost("/", AddVehicleAsync)
            .WithName("addFleetVehicle")
            .RequireFleetSubRole(FleetRoles.Manager);

        // Δ C059: `fleet.yaml` adds vehicles, classifies them and removes them, and gives no way to
        // list them — so SCR-FP-004's status table had no source. Raised in the handoff.
        vehicles.MapGet("/", ListVehiclesAsync)
            .WithName("listFleetVehicles")
            .RequireFleetSubRole(FleetRoles.Viewer);

        // Literal segments before templates: `{vehicleId}` would otherwise swallow `bulk` and fail
        // it as a malformed ULID.
        vehicles.MapPost("/bulk", BulkImportAsync)
            .WithName("bulkAddFleetVehicles")
            .RequireFleetSubRole(FleetRoles.Manager)
            .DisableAntiforgery();

        // Δ C059: the poll the 202's `Location` points at. A job that could only be read once, in
        // the response that created it, is not a job an operator can come back to.
        vehicles.MapGet("/bulk/{jobId}", GetBulkJobAsync)
            .WithName("getBulkVehicleJob")
            .RequireFleetSubRole(FleetRoles.Viewer);

        vehicles.MapDelete("/{vehicleId}", RemoveVehicleAsync)
            .WithName("removeFleetVehicle")
            .RequireFleetSubRole(FleetRoles.Manager);

        vehicles.MapGet("/{vehicleId}/documents", ListDocumentsAsync)
            .WithName("listVehicleDocuments")
            .RequireFleetSubRole(FleetRoles.Viewer);

        vehicles.MapPost("/{vehicleId}/documents", UploadDocumentAsync)
            .WithName("uploadVehicleDocument")
            .RequireFleetSubRole(FleetRoles.Manager)
            .DisableAntiforgery();

        // Δ C059, and outside every group on purpose: the signature in the query string *is* the
        // credential, which is what lets the Fleet Portal hand the link straight to a browser
        // download. Same arrangement as provisioning-svc's `errors.csv` and subscription-svc's
        // signed document links.
        endpoints.MapGet("/v1/fleets/{fleetId}/vehicles/bulk/{jobId}/errors.csv", DownloadErrorReportAsync)
            .WithName("downloadBulkVehicleErrorReport")
            .WithTags("fleet-vehicles")
            .AllowAnonymous();
    }

    private static async Task<Created<FleetVehicleResponse>> AddVehicleAsync(
        AddFleetVehicleBody? body,
        HttpContext context,
        IFleetVehicleService vehicles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(vehicles);

        var scope = context.RequireFleet();

        var added = await vehicles.AddAsync(
            scope.FleetId,
            new AddFleetVehicleCommand(
                body?.RegistrationNumber ?? string.Empty,
                body?.VehicleType?.Trim() ?? string.Empty,
                body?.Mode?.Trim() ?? string.Empty,
                body?.ModeBBilling?.Trim(),
                body?.DefaultMonthlyFareMinor),
            cancellationToken);

        return TypedResults.Created(
            $"/v1/fleets/{scope.FleetId}/vehicles/{added.Vehicle.VehicleId}",
            FleetVehicleResponse.From(added.Vehicle, added.DocsStatus));
    }

    private static async Task<Ok<FleetVehiclesResponse>> ListVehiclesAsync(
        HttpContext context, IFleetVehicleService vehicles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(vehicles);

        var roster = await vehicles.ListAsync(context.RequireFleet().FleetId, cancellationToken);

        return TypedResults.Ok(new FleetVehiclesResponse(
            [.. roster.Select(entry => FleetVehicleResponse.From(entry.Vehicle, entry.DocsStatus))]));
    }

    private static async Task<NoContent> RemoveVehicleAsync(
        string vehicleId, HttpContext context, IFleetVehicleService vehicles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(vehicles);

        await vehicles.RemoveAsync(
            context.RequireFleet().FleetId, RequestIds.Require(vehicleId, "vehicleId"), cancellationToken);

        return TypedResults.NoContent();
    }

    private static async Task<Accepted<BulkJobResponse>> BulkImportAsync(
        HttpContext context,
        IBulkVehicleImportService imports,
        IOptions<FleetOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(imports);
        ArgumentNullException.ThrowIfNull(options);

        var scope = context.RequireFleet();

        if (!context.Request.HasFormContentType)
        {
            throw new MageRideException(
                MageRideErrors.CsvInvalid, "This route takes multipart/form-data with a `file` part.");
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);

        var file = form.Files["file"]
            ?? throw new MageRideException(MageRideErrors.CsvInvalid, "No `file` part in the upload.");

        // Refused at the pipe rather than after buffering: a limit applied to a file already in
        // memory is not a limit. `file.Length` is what the multipart reader counted, not what the
        // client declared.
        if (file.Length > options.Value.BulkUploadMaxBytes)
        {
            throw new MageRideException(
                MageRideErrors.PayloadTooLarge,
                $"The upload is {file.Length} bytes; the limit is {options.Value.BulkUploadMaxBytes}.");
        }

        await using var stream = file.OpenReadStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var result = await imports.ImportAsync(
            scope.FleetId, scope.UserId, await reader.ReadToEndAsync(cancellationToken), cancellationToken);

        return TypedResults.Accepted(
            $"/v1/fleets/{scope.FleetId}/vehicles/bulk/{result.Job.Id}", BulkJobResponse.From(result));
    }

    private static async Task<Ok<BulkJobResponse>> GetBulkJobAsync(
        string jobId, HttpContext context, IBulkVehicleImportService imports, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(imports);

        var job = await imports.GetAsync(
            context.RequireFleet().FleetId, RequestIds.Require(jobId, "jobId"), cancellationToken);

        return TypedResults.Ok(BulkJobResponse.From(job));
    }

    private static async Task<IResult> DownloadErrorReportAsync(
        string fleetId,
        string jobId,
        string? expires,
        string? signature,
        IBulkVehicleImportService imports,
        IErrorReportLinks links,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(imports);
        ArgumentNullException.ThrowIfNull(links);

        var fleet = RequestIds.Require(fleetId, "fleetId");
        var job = RequestIds.Require(jobId, "jobId");

        if (!links.Verify(fleet, job, expires, signature))
        {
            // 404, not 403. The link *is* the credential, so a bad or expired one has not proved
            // that the job exists — and 403 would confirm to somebody guessing job ids that they
            // had found a real one.
            throw new MageRideException(MageRideErrors.NotFound, "This report link is not valid or has expired.");
        }

        var report = await imports.BuildErrorReportAsync(fleet, job, cancellationToken);

        return Results.File(
            Encoding.UTF8.GetBytes(report), "text/csv; charset=utf-8", $"vehicle-import-{job}.csv");
    }

    private static async Task<Ok<VehicleDocumentSlotsResponse>> ListDocumentsAsync(
        string vehicleId, HttpContext context, IVehicleDocumentService documents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(documents);

        var slots = await documents.ListAsync(
            context.RequireFleet().FleetId, RequestIds.Require(vehicleId, "vehicleId"), cancellationToken);

        return TypedResults.Ok(new VehicleDocumentSlotsResponse(
            [.. slots.Select(VehicleDocumentSlotResponse.From)]));
    }

    private static async Task<Created<VehicleDocumentSlotResponse>> UploadDocumentAsync(
        string vehicleId, HttpContext context, IVehicleDocumentService documents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(documents);

        var scope = context.RequireFleet();
        var vehicle = RequestIds.Require(vehicleId, "vehicleId");

        if (!context.Request.HasFormContentType)
        {
            throw new MageRideException(
                MageRideErrors.UnsupportedMediaType,
                "This route takes multipart/form-data with `kind`, `file` and an optional `expiresAt`.");
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        var file = form.Files["file"];

        if (file is null || file.Length == 0)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["file"] = ["A document file is required."],
            });
        }

        DateOnly? expiresAt = null;

        if (form["expiresAt"].ToString() is { Length: > 0 } raw)
        {
            if (!DateOnly.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["expiresAt"] = ["expiresAt must be an ISO date, YYYY-MM-DD."],
                });
            }

            expiresAt = parsed;
        }

        await using var content = file.OpenReadStream();

        var slot = await documents.UploadAsync(
            scope.FleetId,
            vehicle,
            scope.UserId,
            new UploadVehicleDocumentCommand(form["kind"].ToString(), content, expiresAt),
            cancellationToken);

        return TypedResults.Created(
            $"/v1/fleets/{scope.FleetId}/vehicles/{vehicle}/documents",
            VehicleDocumentSlotResponse.From(slot));
    }

    // ---------------------------------------------------------------------------------------
    // Assignment — on C058's other approval-gated group
    // ---------------------------------------------------------------------------------------

    private static void MapAssignmentRoutes(IEndpointRouteBuilder endpoints)
    {
        var assignments = endpoints.MapGroup(FleetEndpoints.FleetAssignmentsGroup)
            .WithTags("fleet-vehicles")
            .RequireAuthorization()
            .AddEndpointFilter<FleetAccessFilter>()
            .RequireApprovedFleet();

        assignments.MapPost("/", AssignAsync)
            .WithName("assignDriverToVehicle")
            .RequireFleetSubRole(FleetRoles.Manager);

        // Δ C059: SCR-FP-005 renders "assign/revoke drivers … assignment history" and the contract
        // has the two writes and no read.
        assignments.MapGet("/", ListAssignmentsAsync)
            .WithName("listFleetAssignments")
            .RequireFleetSubRole(FleetRoles.Viewer);

        assignments.MapDelete("/{assignmentId}", RevokeAsync)
            .WithName("revokeAssignment")
            .RequireFleetSubRole(FleetRoles.Manager);
    }

    private static async Task<Created<AssignmentResponse>> AssignAsync(
        AssignDriverBody? body, HttpContext context, IAssignmentService assignments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(assignments);

        var scope = context.RequireFleet();

        var assigned = await assignments.AssignAsync(
            scope.FleetId,
            new AssignDriverCommand(
                body?.DriverId?.Trim(),
                body?.DriverPhone?.Trim(),
                body?.VehicleId?.Trim(),
                body?.From,
                body?.To),
            cancellationToken);

        return TypedResults.Created(
            $"/v1/fleets/{scope.FleetId}/assignments/{assigned.Id}", AssignmentResponse.From(assigned));
    }

    private static async Task<Ok<AssignmentsResponse>> ListAssignmentsAsync(
        string? vehicleId, HttpContext context, IAssignmentService assignments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(assignments);

        Guid? vehicle = vehicleId is { Length: > 0 }
            ? RequestIds.Require(vehicleId, "vehicleId")
            : null;

        var rows = await assignments.ListAsync(context.RequireFleet().FleetId, vehicle, cancellationToken);

        return TypedResults.Ok(new AssignmentsResponse([.. rows.Select(AssignmentResponse.From)]));
    }

    private static async Task<NoContent> RevokeAsync(
        string assignmentId,
        HttpContext context,
        IAssignmentService assignments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(assignments);

        await assignments.RevokeAsync(
            context.RequireFleet().FleetId, RequestIds.Require(assignmentId, "assignmentId"), cancellationToken);

        return TypedResults.NoContent();
    }

    // ---------------------------------------------------------------------------------------
    // Operations — trackers, schedules, the map, analytics, alerts and geofences
    // ---------------------------------------------------------------------------------------

    private static void MapOperationsRoutes(IEndpointRouteBuilder endpoints, FleetOptions settings)
    {
        // Reads live here and are NOT approval-gated: US-13.A7 disables onboarding and assignment,
        // not monitoring, and an organisation waiting on a Verification Officer still has to be able
        // to watch the vehicles it already runs.
        var ops = endpoints.MapGroup(FleetOpsGroup)
            .WithTags("fleet-ops")
            .RequireAuthorization()
            .AddEndpointFilter<FleetAccessFilter>();

        ops.MapGet("/map", ReadMapAsync)
            .WithName("getFleetMap")
            .RequireFleetSubRole(FleetRoles.Viewer);

        ops.MapGet("/analytics", ReadAnalyticsAsync)
            .WithName("getFleetAnalytics")
            .RequireFleetSubRole(FleetRoles.Viewer);

        ops.MapGet("/alerts", ListAlertsAsync)
            .WithName("listFleetAlerts")
            .RequireFleetSubRole(FleetRoles.Viewer);

        ops.MapGet("/schedules", ListSchedulesAsync)
            .WithName("listFleetSchedules")
            .RequireFleetSubRole(FleetRoles.Viewer);

        ops.MapGet("/geofences", ListGeofencesAsync)
            .WithName("listFleetGeofences")
            .RequireFleetSubRole(FleetRoles.Viewer);

        // The writes, each gated on approval individually — this group is shared with the reads
        // above, so the gate cannot live on the builder.
        ops.MapPut("/geofences", SetGeofencesAsync)
            .WithName("setFleetGeofences")
            .RequireFleetSubRole(FleetRoles.Manager)
            .RequireApprovedFleet();

        ops.MapPost("/schedules", CreateScheduleAsync)
            .WithName("createFleetSchedule")
            .RequireFleetSubRole(FleetRoles.Manager)
            .RequireApprovedFleet();

        // Without provisioning-svc there is no credential to mint, so the route is not mapped at
        // all rather than accepting a bind and doing nothing — an operator would otherwise believe
        // an ST-901 was armed on a bus that is not being tracked.
        if (!string.IsNullOrWhiteSpace(settings.ProvisioningBaseUrl))
        {
            ops.MapPost("/trackers/bind", BindTrackerAsync)
                .WithName("bindFleetTracker")
                .RequireFleetSubRole(FleetRoles.Manager)
                .RequireApprovedFleet();
        }
    }

    private static async Task<Ok<FleetMapResponse>> ReadMapAsync(
        HttpContext context, IFleetInsightsService insights, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(insights);

        var (vehicles, asOf) = await insights.ReadMapAsync(context.RequireFleet().FleetId, cancellationToken);

        return TypedResults.Ok(new FleetMapResponse(
            [.. vehicles.Select(FleetVehiclePositionResponse.From)], asOf));
    }

    private static async Task<Ok<FleetAnalyticsResponse>> ReadAnalyticsAsync(
        string? from,
        string? to,
        HttpContext context,
        IFleetInsightsService insights,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(insights);

        var items = await insights.ReadAnalyticsAsync(
            context.RequireFleet().FleetId,
            RequestDates.Optional(from, "from"),
            RequestDates.Optional(to, "to"),
            cancellationToken);

        return TypedResults.Ok(new FleetAnalyticsResponse([.. items.Select(VehicleAnalyticsResponse.From)]));
    }

    /// <summary>
    /// US-13.5's alert page — empty, in Phase 1, by construction.
    /// </summary>
    /// <remarks>
    /// Mapped rather than omitted so the Fleet Portal can render its empty state now and gain rows
    /// later with no breaking change, which is what <c>fleet.yaml</c>'s own description asks for.
    /// Nothing on this platform emits a route-deviation or geofence alert: there is no producer, no
    /// table and no consumer, and a stub that invented one would be worse than an honest emptiness.
    /// </remarks>
    private static Ok<FleetAlertsResponse> ListAlertsAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _ = context.RequireFleet();

        return TypedResults.Ok(new FleetAlertsResponse([], null, false));
    }

    private static async Task<Ok<FleetSchedulesResponse>> ListSchedulesAsync(
        string? from,
        HttpContext context,
        IScheduleService schedules,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(schedules);
        ArgumentNullException.ThrowIfNull(clock);

        // A day back by default, so the departures whose alarm just rang are on the screen an
        // operator opens to find out why.
        var since = from is { Length: > 0 } && DateTimeOffset.TryParse(
            from, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : clock.GetUtcNow().AddDays(-1);

        var rows = await schedules.ListAsync(context.RequireFleet().FleetId, since, cancellationToken);

        return TypedResults.Ok(new FleetSchedulesResponse([.. rows.Select(FleetScheduleResponse.From)]));
    }

    private static async Task<Created<FleetScheduleResponse>> CreateScheduleAsync(
        CreateScheduleBody? body, HttpContext context, IScheduleService schedules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(schedules);

        var scope = context.RequireFleet();

        var created = await schedules.CreateAsync(
            scope.FleetId,
            scope.UserId,
            new CreateScheduleCommand(
                body?.VehicleId?.Trim(), body?.RouteId?.Trim(), body?.DepartAt, body?.NotStartedAlarmMinutes),
            cancellationToken);

        return TypedResults.Created(
            $"/v1/fleets/{scope.FleetId}/schedules/{created.Id}", FleetScheduleResponse.From(created));
    }

    private static async Task<Created<TrackerBindingResponse>> BindTrackerAsync(
        BindTrackerBody? body, HttpContext context, ITrackerBindingService trackers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(trackers);

        var scope = context.RequireFleet();

        var bearer = context.Request.Headers.Authorization.ToString();
        var token = bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? bearer[7..] : bearer;

        var bound = await trackers.BindTrackerAsync(
            scope.FleetId,
            token,
            new BindTrackerCommand(body?.Imei, body?.VehicleId?.Trim(), body?.AutoStartSession ?? true),
            cancellationToken);

        return TypedResults.Created(
            $"/v1/trackers/{bound.Imei}", TrackerBindingResponse.From(bound));
    }

    private static async Task<Ok<GeofencesResponse>> ListGeofencesAsync(
        HttpContext context, IFleetInsightsService insights, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(insights);

        var geofences = await insights.ListGeofencesAsync(context.RequireFleet().FleetId, cancellationToken);

        return TypedResults.Ok(new GeofencesResponse([.. geofences.Select(GeofenceResponse.From)]));
    }

    private static async Task<Ok<GeofenceCountResponse>> SetGeofencesAsync(
        SetGeofencesBody? body, HttpContext context, IFleetInsightsService insights, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(insights);

        var count = await insights.ReplaceGeofencesAsync(
            context.RequireFleet().FleetId,
            [
                .. (body?.Geofences ?? []).Select(geofence => new GeofenceDraft(
                    geofence.Name,
                    [.. (geofence.Polygon ?? []).Select(point => new GeoPointDraft(point.Lat, point.Lng))])),
            ],
            cancellationToken);

        return TypedResults.Ok(new GeofenceCountResponse(count));
    }

    // ---------------------------------------------------------------------------------------
    // Mode B subscription proxies (Δ 2026-06-21 items 15, 16, 17 — SCR-FP-011/012)
    // ---------------------------------------------------------------------------------------

    private static void MapSubscriptionProxies(IEndpointRouteBuilder endpoints)
    {
        // On the vehicle group, so the approval gate and the org scope apply to the Epic 23 surface
        // exactly as they do to the roster — an unapproved organisation has no subscribers to
        // manage, and would not be able to have classified a vehicle Paid in the first place.
        var proxied = endpoints.MapGroup(FleetEndpoints.FleetVehiclesGroup)
            .WithTags("fleet-subscriptions")
            .RequireAuthorization()
            .AddEndpointFilter<FleetAccessFilter>()
            .RequireApprovedFleet();

        // Manager for the queue and the roster — US-23.1 gives "Owner/Manager … the same
        // accept/reject". subscription-svc re-checks the same thing against the vehicle, so this is
        // the coarse half of a two-part answer, not the whole of it.
        proxied.MapGet("/{vehicleId}/requests", ProxyAsync("requests", HttpMethod.Get, "{vehicleId}/access-requests"))
            .WithName("listFleetVehicleRequests")
            .RequireFleetSubRole(FleetRoles.Manager);

        proxied.MapPost(
                "/{vehicleId}/requests/{requestId}/accept",
                ProxyAsync("accept", HttpMethod.Post, "access-requests/{requestId}/accept"))
            .WithName("acceptFleetVehicleRequest")
            .RequireFleetSubRole(FleetRoles.Manager);

        proxied.MapPost(
                "/{vehicleId}/requests/{requestId}/reject",
                ProxyAsync("reject", HttpMethod.Post, "access-requests/{requestId}/reject"))
            .WithName("rejectFleetVehicleRequest")
            .RequireFleetSubRole(FleetRoles.Manager);

        proxied.MapGet("/{vehicleId}/subscribers", ProxyAsync("roster", HttpMethod.Get, "{vehicleId}/subscribers"))
            .WithName("listFleetVehicleSubscribers")
            .RequireFleetSubRole(FleetRoles.Manager);

        // Owner from here down. US-23.6 is explicit — "only the fleet Owner can mark it received" —
        // and US-23.7 and item 17 put the fare override and the hard delete in the same hands.
        proxied.MapDelete(
                "/{vehicleId}/subscribers/{subscriberId}",
                ProxyAsync("delete-subscriber", HttpMethod.Delete, "{vehicleId}/subscribers/{subscriberId}"))
            .WithName("deleteFleetVehicleSubscriber")
            .RequireFleetSubRole(FleetRoles.Owner);

        proxied.MapPut(
                "/{vehicleId}/subscribers/{subscriberId}/fare",
                ProxyAsync("fare", HttpMethod.Put, "{vehicleId}/subscribers/{subscriberId}/fare"))
            .WithName("setFleetSubscriberFare")
            .RequireFleetSubRole(FleetRoles.Owner);

        proxied.MapPost(
                "/{vehicleId}/subscribers/{subscriberId}/mark-cash",
                ProxyAsync("mark-cash", HttpMethod.Post, "{vehicleId}/subscribers/{subscriberId}/mark-cash"))
            .WithName("markFleetSubscriberCashPaid")
            .RequireFleetSubRole(FleetRoles.Owner);

        proxied.MapGet(
                "/{vehicleId}/subscribers/{subscriberId}/payments",
                ProxyAsync("payments", HttpMethod.Get, "{vehicleId}/subscribers/{subscriberId}/payments"))
            .WithName("listFleetSubscriberPayments")
            .RequireFleetSubRole(FleetRoles.Owner);

        // The one proxy addressed by payment rather than by vehicle. It hangs off the fleet group
        // instead, and carries the org scope through the caller's bearer alone — subscription-svc
        // resolves the payment's own vehicle and checks ownership against it (C048).
        var payments = endpoints.MapGroup("/v1/fleets/{fleetId}/payments")
            .WithTags("fleet-subscriptions")
            .RequireAuthorization()
            .AddEndpointFilter<FleetAccessFilter>()
            .RequireApprovedFleet();

        payments.MapPost("/{paymentId}/confirm", ProxyAsync("confirm", HttpMethod.Post, "payments/{paymentId}/confirm"))
            .WithName("confirmFleetTransferSlip")
            .RequireFleetSubRole(FleetRoles.Owner);
    }

    /// <summary>
    /// Builds a handler that forwards to subscription-svc's <c>/v1/mode-b</c> spelling of the route.
    /// </summary>
    /// <remarks>
    /// The upstream path is built from route values this service has already parsed — never from
    /// raw request text — so a template segment cannot smuggle a path of its own. The
    /// <paramref name="what"/> label exists so a log line names the operation rather than a URL.
    /// </remarks>
    private static Delegate ProxyAsync(string what, HttpMethod method, string template) =>
        async (HttpContext context, ISubscriptionProxy proxy, CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(proxy);

            var scope = context.RequireFleet();

            Guid? vehicleId = context.Request.RouteValues.TryGetValue("vehicleId", out var raw)
                ? RequestIds.Require(raw?.ToString(), "vehicleId")
                : null;

            var path = template;

            foreach (var (key, value) in context.Request.RouteValues)
            {
                if (value is not null && !string.Equals(key, "fleetId", StringComparison.Ordinal))
                {
                    // Every substituted value is an identifier this service parsed as a ULID or a
                    // UUID, so the result cannot carry a slash, a query string or a traversal.
                    path = path.Replace(
                        $"{{{key}}}",
                        Uri.EscapeDataString(RequestIds.Require(value.ToString(), key).ToString()),
                        StringComparison.Ordinal);
                }
            }

            await proxy.ForwardAsync(context, scope.FleetId, vehicleId, method, path, cancellationToken);
        };
}

/// <summary>Parses the ISO dates the analytics filters take.</summary>
internal static class RequestDates
{
    public static DateOnly? Optional(string? value, string field)
    {
        if (value is not { Length: > 0 })
        {
            return null;
        }

        return DateOnly.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [field] = [$"{field} must be an ISO date, YYYY-MM-DD."],
            });
    }
}
