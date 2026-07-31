using MageRide.Fleet.Configuration;
using MageRide.Fleet.Domain;
using MageRide.Fleet.Persistence;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using MageRide.Shared.Primitives;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace MageRide.Fleet.Authorization;

/// <summary>
/// What the filter resolved about this request: which organisation, and the caller's seat in it.
/// </summary>
/// <param name="FleetRole">
/// The sub-role from <c>iam.fleet_members</c> for <em>this</em> organisation — not the token's
/// <c>fleet_role</c> claim, which carries only the most privileged of a person's memberships
/// (C027).
/// </param>
public sealed record FleetContext(Guid FleetId, Guid UserId, string FleetRole, FleetOrganisation Fleet);

/// <summary>Endpoint metadata: the minimum sub-role a route needs (AL-03, US-13.A5).</summary>
public sealed record RequiredFleetRole(string MinimumFleetRole);

/// <summary>Endpoint metadata: the route is refused until a Verification Officer approves the org.</summary>
public sealed record RequiresApprovedFleet;

/// <summary>
/// Resolves the caller's standing in the organisation named in the path, and refuses the request
/// when it is not enough.
/// </summary>
/// <remarks>
/// <para>
/// <b>Four checks, in this order, on every fleet-scoped route.</b>
/// </para>
/// <list type="number">
/// <item>the path names a real organisation — <c>404 fleet-not-found</c>;</item>
/// <item>the caller holds a membership of <em>that</em> organisation — <c>403 not-fleet-member</c>;</item>
/// <item>the membership is at least the sub-role the route declares — <c>403 fleet-role-insufficient</c>;</item>
/// <item>the organisation is APPROVED, when the route declares it — <c>403 fleet-not-approved</c>.</item>
/// </list>
/// <para>
/// <b>Why the membership is read rather than taken from the token.</b> A person may belong to
/// several organisations; iam-svc puts one <c>fleet_role</c>/<c>fleet_id</c> pair in the token and
/// picks the most privileged. Trusting the claim would mean an Owner of one fleet arrived at
/// another fleet's path as an owner. The claim gets the request past deny-by-default
/// authorization; this filter decides what it may actually do.
/// </para>
/// <para>
/// <b>Why the gate is metadata and not an <c>if</c> in each handler.</b> The definition of done is
/// "an unapproved org receives 403 on <em>every</em> vehicle and assignment endpoint", and a check
/// that each handler has to remember is one merge away from being forgotten. The two groups
/// <c>FleetEndpoints</c> builds carry <see cref="RequiresApprovedFleet"/>, so a route added to
/// them later — C059's onboarding, bulk CSV, assignment, tracker binding — is gated the moment it
/// is mapped. <c>Every_vehicle_and_assignment_route_is_gated</c> asserts it by walking the
/// endpoint data source, so the guarantee survives the component that added it.
/// </para>
/// <para>
/// <b>404 before 403 on the organisation, 403 before 404 on everything else.</b> A fleet id is a
/// UUID nobody guesses, so answering "no such organisation" leaks nothing; inside one, a caller who
/// is not a member must not be able to tell an id that exists from one that does not, which is why
/// the membership check comes second and every later refusal is a 403.
/// </para>
/// </remarks>
internal sealed class FleetAccessFilter(
    IUnitOfWorkFactory unitOfWorkFactory,
    IFleetRepository fleets,
    IFleetMemberRepository members,
    IOptions<FleetOptions> options) : IEndpointFilter
{
    /// <summary>Route parameter every fleet-scoped path uses (<c>fleet.yaml</c> <c>FleetId</c>).</summary>
    public const string RouteParameter = "fleetId";

    private readonly FleetOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var http = context.HttpContext;
        var endpoint = http.GetEndpoint();

        var raw = http.Request.RouteValues.TryGetValue(RouteParameter, out var value) ? value?.ToString() : null;

        if (!Ulids.TryParse(raw, out var fleetId) || fleetId == Guid.Empty)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [RouteParameter] = ["fleetId is required and must be a ULID or a UUID."],
            });
        }

        var userId = http.User.SubjectId()
            ?? throw new MageRideException(MageRideErrors.Unauthorized, "The token carries no usable 'sub' claim.");

        // One transaction for the two reads, so the organisation's status and the caller's seat in
        // it are the same instant: an officer approving between them would otherwise let a request
        // be refused for a state that had already changed.
        //
        // Not a fleet-scoped read: this *is* the call that establishes the scope, and the reader
        // role can see nothing until it knows which organisation to be scoped to.
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: http.RequestAborted);

        var fleet = await fleets.FindAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fleetId, http.RequestAborted);

        if (fleet is null)
        {
            throw new MageRideException(FleetErrors.FleetNotFound, "No such fleet organisation.");
        }

        var fleetRole = await members.RoleForAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fleetId, userId, http.RequestAborted);

        await unitOfWork.CommitAsync(http.RequestAborted);

        if (fleetRole is null)
        {
            throw new MageRideException(
                FleetErrors.NotFleetMember, "The caller holds no sub-role in this fleet organisation.");
        }

        if (endpoint?.Metadata.GetMetadata<RequiredFleetRole>() is { } required
            && !FleetRoles.Satisfies(fleetRole, required.MinimumFleetRole))
        {
            throw new MageRideException(
                FleetErrors.FleetRoleInsufficient,
                $"This needs the {required.MinimumFleetRole} sub-role; the caller holds {fleetRole}.");
        }

        if (endpoint?.Metadata.GetMetadata<RequiresApprovedFleet>() is not null
            && _options.VerificationGate
            && !fleet.IsApproved)
        {
            throw new MageRideException(
                FleetErrors.FleetNotApproved,
                "A Verification Officer must approve the organisation before it can onboard vehicles or assign drivers (US-13.A7).");
        }

        http.Items[typeof(FleetContext)] = new FleetContext(fleetId, userId, fleetRole, fleet);

        return await next(context);
    }
}

/// <summary>Reads what <see cref="FleetAccessFilter"/> resolved.</summary>
public static class FleetContextAccessor
{
    /// <summary>
    /// The request's fleet context, or a throw.
    /// </summary>
    /// <remarks>
    /// A throw rather than a nullable return: every caller is a handler on a route the filter runs
    /// on, so a missing context is a route that was mapped outside the group — a wiring mistake
    /// that should fail loudly in a test, not degrade into an unscoped request.
    /// </remarks>
    public static FleetContext RequireFleet(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items.TryGetValue(typeof(FleetContext), out var value) && value is FleetContext fleet
            ? fleet
            : throw new InvalidOperationException(
                "No FleetContext on the request. The route was mapped outside a group carrying FleetAccessFilter.");
    }
}

/// <summary>Declares what a fleet-scoped route needs. The filter enforces it.</summary>
public static class FleetEndpointConventions
{
    /// <summary>Requires a sub-role of at least <paramref name="minimumFleetRole"/> for the org in the path.</summary>
    public static TBuilder RequireFleetSubRole<TBuilder>(this TBuilder builder, string minimumFleetRole)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!FleetRoles.All.Contains(minimumFleetRole))
        {
            throw new ArgumentException($"'{minimumFleetRole}' is not a fleet sub-role (AL-03).", nameof(minimumFleetRole));
        }

        return builder.WithMetadata(new RequiredFleetRole(minimumFleetRole));
    }

    /// <summary>Refuses the route until a Verification Officer has approved the org (US-13.A7).</summary>
    public static TBuilder RequireApprovedFleet<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(new RequiresApprovedFleet());
    }
}
