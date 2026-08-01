using MageRide.FleetBilling.Domain;
using MageRide.FleetBilling.Persistence;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Primitives;
using Microsoft.AspNetCore.Http;

namespace MageRide.FleetBilling.Authorization;

/// <summary>What the filter resolved: which organisation, and the caller's seat in it.</summary>
public sealed record FleetBillingContext(Guid FleetId, Guid UserId, string FleetRole, string FleetName);

/// <summary>
/// Resolves the caller's standing in the organisation named in the path, and refuses the request
/// when it is not enough.
/// </summary>
/// <remarks>
/// <para>
/// <b>Four checks on every route this service serves, in this order.</b>
/// </para>
/// <list type="number">
/// <item>the path names a real organisation — <c>404 fleet-not-found</c>;</item>
/// <item>the caller holds a membership of <em>that</em> organisation — <c>403 not-fleet-member</c>;</item>
/// <item>the membership is <b>Owner</b> — <c>403 fleet-role-insufficient</c>;</item>
/// <item>the organisation is APPROVED — <c>403 fleet-not-approved</c>.</item>
/// </list>
/// <para>
/// <b>Owner and nobody else, on reads as well as writes.</b> US-13.A5 gives the Fleet Owner "full
/// org control + billing" and takes billing away from the Manager in the same sentence — "Manager =
/// onboarding/assignment/scheduling/monitoring (no billing/owner changes)" — and the Viewer is
/// "read-only fleet map &amp; analytics", which is not the ledger. C027's <c>PolicyEvaluator</c>
/// narrows a Manager out of <c>fleet-billing</c> for the same reason. So unlike fleet-svc, where the
/// map and the analytics sit outside the role gate, <b>every</b> route here carries it: there is no
/// billing read a Manager is entitled to.
/// </para>
/// <para>
/// <b>The membership is read, never taken from the token.</b> A person may belong to several
/// organisations; iam-svc puts one <c>fleet_role</c>/<c>fleet_id</c> pair in the token and picks the
/// most privileged (C027). Trusting the claim would mean an Owner of one fleet arriving at another
/// fleet's invoices as an owner. The claim gets the request past deny-by-default authorization; this
/// filter decides what it may actually do. fleet-svc's rule, and it has to be the same rule or the
/// two halves of the Fleet Portal would disagree about who may act.
/// </para>
/// <para>
/// <b>Approval gates billing, and here it gates reading too.</b> fleet-svc deliberately leaves the
/// map and the analytics open to a PENDING organisation, because an operator waiting for a
/// Verification Officer must still be able to watch vehicles they already run. Billing is the
/// opposite case: a PENDING org has no approved vehicles, so it has no charges, no invoice and no
/// wallet — every route here would answer an empty page, and an empty page is a worse answer than
/// "your organisation is still being reviewed".
/// </para>
/// <para>
/// <b>404 before 403 on the organisation, 403 on everything inside it.</b> A fleet id is a UUID
/// nobody guesses, so "no such organisation" leaks nothing; inside one, a caller who is not a member
/// must not be able to tell an id that exists from one that does not.
/// </para>
/// </remarks>
internal sealed class FleetBillingAccessFilter(IFleetAccessRepository fleets) : IEndpointFilter
{
    /// <summary>Route parameter every fleet-scoped path uses (<c>fleet-billing.yaml</c>'s <c>FleetId</c>).</summary>
    public const string RouteParameter = "fleetId";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var http = context.HttpContext;

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

        var fleet = await fleets.FindAsync(fleetId, http.RequestAborted)
                    ?? throw new MageRideException(FleetBillingErrors.FleetNotFound, "No such fleet organisation.");

        var fleetRole = await fleets.RoleForAsync(fleetId, userId, http.RequestAborted)
                        ?? throw new MageRideException(
                            FleetBillingErrors.NotFleetMember,
                            "The caller holds no sub-role in this fleet organisation.");

        // The kernel's ladder (MageRideClaims.FleetRoles), not a copy: iam-svc mints the
        // `fleet_role` claim against it and fleet-svc gates on it, and three services ranking
        // Owner/Manager/Viewer differently is how one half of the Fleet Portal ends up admitting
        // somebody the other half refuses.
        if (!MageRide.Shared.Auth.FleetRoles.Satisfies(fleetRole, MageRide.Shared.Auth.FleetRoles.Owner))
        {
            throw new MageRideException(
                FleetBillingErrors.FleetRoleInsufficient,
                $"Billing is the Owner's (US-13.A5); the caller holds {fleetRole}.");
        }

        if (!fleet.IsApproved)
        {
            throw new MageRideException(
                FleetBillingErrors.FleetNotApproved,
                "A Verification Officer must approve the organisation before it is billed (US-13.A7). A "
                + "pending organisation has no approved vehicles, so it has no charges and no invoice.");
        }

        http.Items[typeof(FleetBillingContext)] = new FleetBillingContext(fleetId, userId, fleetRole, fleet.Name);

        return await next(context);
    }
}

/// <summary>Reads what <see cref="FleetBillingAccessFilter"/> resolved.</summary>
public static class FleetBillingContextAccessor
{
    /// <summary>The request's fleet context, or a throw.</summary>
    /// <remarks>
    /// A throw rather than a nullable return: every caller is a handler on a route the filter runs
    /// on, so a missing context is a route mapped outside the group — a wiring mistake that should
    /// fail loudly in a test rather than degrade into an unscoped request.
    /// </remarks>
    public static FleetBillingContext RequireFleet(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items.TryGetValue(typeof(FleetBillingContext), out var value)
               && value is FleetBillingContext fleet
            ? fleet
            : throw new InvalidOperationException(
                "No FleetBillingContext on the request. The route was mapped outside the group carrying "
                + "FleetBillingAccessFilter.");
    }
}
