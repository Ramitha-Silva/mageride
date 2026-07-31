using MageRide.Fleet.Authorization;
using MageRide.Fleet.Domain;
using MageRide.Fleet.Organisation;
using MageRide.Fleet.Payouts;
using MageRide.Fleet.Vehicles;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MageRide.Fleet.Endpoints;

/// <summary>
/// <c>/v1/fleets</c> — the Fleet Portal's organisation surface (AL-03, AL-49).
/// </summary>
/// <remarks>
/// <para>
/// <b>The route table is <c>backend/contracts/fleet.yaml</c>, and what is mapped here is C058's
/// share of it:</b> the organisation, its team, its payout profile, and the one vehicle route
/// AL-49's gate lives on. Vehicle onboarding, documents, assignment, tracker binding, scheduling,
/// the map, analytics, geofences, billing and the subscription proxies are C059's and C060's.
/// </para>
/// <para>
/// <b>C059 hangs its routes off <see cref="FleetVehiclesGroup"/> and
/// <see cref="FleetAssignmentsGroup"/>.</b> Both carry <c>RequireApprovedFleet()</c>, so a route
/// added to either is gated by US-13.A7 the moment it is mapped rather than each remembering to
/// check. That is the structural form of "an unapproved org receives 403 on every vehicle and
/// assignment endpoint", and a test walks the endpoint data source to keep it true.
/// </para>
/// </remarks>
public static class FleetEndpoints
{
    /// <summary>Prefix every vehicle route lives under. The approval gate is on the group.</summary>
    public const string FleetVehiclesGroup = "/v1/fleets/{fleetId}/vehicles";

    /// <summary>Prefix every driver-assignment route lives under. Same gate.</summary>
    public const string FleetAssignmentsGroup = "/v1/fleets/{fleetId}/assignments";

    public static IEndpointRouteBuilder MapFleetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // ------------------------------------------------------------------------------------
        // Registration — the one route with no organisation to be scoped to yet.
        // ------------------------------------------------------------------------------------
        var registration = endpoints.MapGroup("/v1/fleets").WithTags("fleets");

        // fleet_owner, the canonical role (AL-06). The sub-role model starts at the membership
        // this call creates, so there is nothing for FleetAccessFilter to resolve.
        registration.MapPost("/", RegisterFleetAsync)
            .WithName("registerFleet")
            .RequireMageRideRole(MageRideRoles.FleetOwner);

        // ------------------------------------------------------------------------------------
        // Everything addressed by {fleetId}. FleetAccessFilter resolves the caller's seat from
        // iam.fleet_members — never from the token's claim, which carries only one membership.
        // ------------------------------------------------------------------------------------
        var fleet = endpoints.MapGroup("/v1/fleets/{fleetId}")
            .WithTags("fleets")
            .RequireAuthorization()
            .AddEndpointFilter<FleetAccessFilter>();

        // Any sub-role, and deliberately not gated on approval: US-13.A7 disables onboarding and
        // assignment, not reading. A PENDING organisation's owner needs to see that it is pending.
        fleet.MapGet("/", GetFleetAsync)
            .WithName("getFleet")
            .RequireFleetSubRole(FleetRoles.Viewer);

        fleet.MapPost("/members", AddMemberAsync)
            .WithName("addFleetMember")
            .RequireFleetSubRole(FleetRoles.Owner);

        // Δ C058: `fleet.yaml` provisions members and gives no way to read them back, so the
        // portal cannot render SCR-FP-002's team list. Raised in the handoff.
        fleet.MapGet("/members", ListMembersAsync)
            .WithName("listFleetMembers")
            .RequireFleetSubRole(FleetRoles.Viewer);

        // Owner only, all three. US-13.A5: "Manager = onboarding/assignment/scheduling/monitoring
        // (no billing/owner changes)", and the account the organisation's money arrives in is the
        // most owner-ish thing on the portal. C027's PolicyEvaluator narrows a Manager out of
        // `fleet-billing` for the same reason.
        fleet.MapGet("/payout-profile", GetPayoutProfileAsync)
            .WithName("getPayoutProfile")
            .RequireFleetSubRole(FleetRoles.Owner);

        fleet.MapPut("/payout-profile", UpsertPayoutProfileAsync)
            .WithName("upsertPayoutProfile")
            .RequireFleetSubRole(FleetRoles.Owner);

        // DisableAntiforgery for the reason support-svc's screenshot and ride-svc's proof photo
        // do: a Bearer-authenticated multipart POST from a portal fetch, not a browser form, so
        // there is no cookie to protect and no token to carry.
        fleet.MapPost("/payout-profile/documents", UploadPayoutDocumentAsync)
            .WithName("uploadPayoutProfileDocument")
            .RequireFleetSubRole(FleetRoles.Owner)
            .DisableAntiforgery();

        // ------------------------------------------------------------------------------------
        // The two gated groups. Everything under them is refused until a Verification Officer has
        // approved the organisation (US-13.A7) — including routes this component did not write.
        // ------------------------------------------------------------------------------------
        var vehicles = endpoints.MapGroup(FleetVehiclesGroup)
            .WithTags("fleet-vehicles")
            .RequireAuthorization()
            .AddEndpointFilter<FleetAccessFilter>()
            .RequireApprovedFleet();

        vehicles.MapPut("/{vehicleId}/classification", SetClassificationAsync)
            .WithName("setVehicleClassification")
            .RequireFleetSubRole(FleetRoles.Manager);

        // Mapped with no routes on it yet: C059 owns POST /assignments and DELETE
        // /assignments/{id}. The group exists here so those land inside the gate rather than
        // beside it — the group builder is what carries RequireApprovedFleet, and a route mapped
        // on a group created later would not.
        _ = endpoints.MapGroup(FleetAssignmentsGroup)
            .WithTags("fleet-vehicles")
            .RequireAuthorization()
            .AddEndpointFilter<FleetAccessFilter>()
            .RequireApprovedFleet();

        return endpoints;
    }

    // ---------------------------------------------------------------------------------------
    // Organisation
    // ---------------------------------------------------------------------------------------

    private static async Task<Created<FleetResponse>> RegisterFleetAsync(
        RegisterFleetBody? body,
        HttpContext context,
        IFleetService fleets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(fleets);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var name = FleetRequests.RequireText(errors, body?.Name, "name", 200);
        var registrationNo = FleetRequests.RequireText(errors, body?.RegistrationNo, "registrationNo", 64);
        var contactPhone = FleetRequests.RequirePhone(errors, body?.ContactPhone, "contactPhone");
        var contactEmail = body?.ContactEmail is null
            ? null
            : FleetRequests.RequireEmail(errors, body.ContactEmail, "contactEmail");
        var address = FleetRequests.OptionalText(errors, body?.Address, "address", 500);

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }

        var fleet = await fleets.RegisterAsync(
            new RegisterFleetCommand(
                context.User.RequireSubjectId(), name, registrationNo, contactPhone, contactEmail, address),
            cancellationToken);

        return TypedResults.Created($"/v1/fleets/{fleet.Id}", FleetResponse.From(fleet));
    }

    private static async Task<Ok<FleetResponse>> GetFleetAsync(
        HttpContext context, IFleetService fleets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(fleets);

        // Read through the row-level-security scope rather than returned from the filter's own
        // lookup. The filter read it as the service's login role to decide whether the caller may
        // be here at all; what the caller is *shown* goes through the reader role, so the response
        // is the database's answer to "what may this organisation see", not the application's.
        var fleet = await fleets.ReadAsync(context.RequireFleet().FleetId, cancellationToken);

        return TypedResults.Ok(FleetResponse.From(fleet));
    }

    // ---------------------------------------------------------------------------------------
    // Members (US-13.A5)
    // ---------------------------------------------------------------------------------------

    private static async Task<Created<FleetMemberResponse>> AddMemberAsync(
        AddFleetMemberBody? body,
        HttpContext context,
        IFleetService fleets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(fleets);

        var scope = context.RequireFleet();
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var email = FleetRequests.RequireEmail(errors, body?.Email, "email");
        var name = FleetRequests.OptionalText(errors, body?.Name, "name", 200);
        var fleetRole = body?.FleetRole?.Trim();

        // Manager and Viewer only. US-13.A5 gives the Fleet Owner "team members for the Manager
        // and Viewer roles"; a second Owner is a change of who the organisation belongs to, and
        // `registry.fleets.owner_id` — which nothing here rewrites — says it is not this route's
        // to make.
        if (fleetRole is not (FleetRoles.Manager or FleetRoles.Viewer))
        {
            errors["fleetRole"] = ["fleetRole must be 'manager' or 'viewer'."];
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }

        var member = await fleets.AddMemberAsync(
            scope.FleetId, scope.UserId, email, name, fleetRole!, cancellationToken);

        return TypedResults.Created(
            $"/v1/fleets/{scope.FleetId}/members/{member.UserId}", FleetMemberResponse.From(member));
    }

    private static async Task<Ok<FleetMembersResponse>> ListMembersAsync(
        HttpContext context, IFleetService fleets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(fleets);

        var members = await fleets.ListMembersAsync(context.RequireFleet().FleetId, cancellationToken);

        return TypedResults.Ok(new FleetMembersResponse([.. members.Select(FleetMemberResponse.From)]));
    }

    // ---------------------------------------------------------------------------------------
    // Payout profile (AL-49, SCR-FP-002a)
    // ---------------------------------------------------------------------------------------

    private static async Task<Ok<PayoutProfileResponse>> GetPayoutProfileAsync(
        HttpContext context, IPayoutProfileService payouts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payouts);

        var profile = await payouts.ReadAsync(context.RequireFleet().FleetId, cancellationToken);

        return TypedResults.Ok(PayoutProfileResponse.From(profile));
    }

    private static async Task<Ok<PayoutProfileResponse>> UpsertPayoutProfileAsync(
        PayoutProfileBody? body,
        HttpContext context,
        IPayoutProfileService payouts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payouts);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var bank = FleetRequests.RequireText(errors, body?.Bank, "bank", 120);
        var branch = FleetRequests.RequireText(errors, body?.Branch, "branch", 120);
        var accountNo = FleetRequests.RequireText(errors, body?.AccountNo, "accountNo", 40);
        var holder = FleetRequests.RequireText(errors, body?.AccountHolderName, "accountHolderName", 200);

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }

        var profile = await payouts.UpsertAsync(
            context.RequireFleet().FleetId,
            new PayoutProfileDraft(bank, branch, accountNo, holder),
            cancellationToken);

        return TypedResults.Ok(PayoutProfileResponse.From(profile));
    }

    private static async Task<Created<PayoutDocumentResponse>> UploadPayoutDocumentAsync(
        HttpContext context, IPayoutProfileService payouts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payouts);

        var scope = context.RequireFleet();

        if (!context.Request.HasFormContentType)
        {
            throw new MageRideException(
                MageRideErrors.UnsupportedMediaType, "This route takes multipart/form-data with `kind` and `file`.");
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        var kind = form["kind"].ToString().Trim();
        var file = form.Files["file"];

        if (file is null || file.Length == 0)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["file"] = ["A document file is required."],
            });
        }

        await using var content = file.OpenReadStream();

        var attached = await payouts.AttachDocumentAsync(
            scope.FleetId, scope.UserId, kind, content, cancellationToken);

        return TypedResults.Created(
            $"/v1/fleets/{scope.FleetId}/payout-profile",
            new PayoutDocumentResponse(attached.DocId.ToString(), attached.Kind));
    }

    // ---------------------------------------------------------------------------------------
    // The AL-49 gate (BR-31.1)
    // ---------------------------------------------------------------------------------------

    private static async Task<Ok<FleetVehicleResponse>> SetClassificationAsync(
        string vehicleId,
        ClassificationBody? body,
        HttpContext context,
        IClassificationService classification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(classification);

        var vehicle = await classification.SetAsync(
            context.RequireFleet().FleetId,
            RequestIds.Require(vehicleId, "vehicleId"),
            body?.ModeBBilling?.Trim() ?? string.Empty,
            body?.DefaultMonthlyFareMinor,
            cancellationToken);

        return TypedResults.Ok(FleetVehicleResponse.From(vehicle));
    }
}
