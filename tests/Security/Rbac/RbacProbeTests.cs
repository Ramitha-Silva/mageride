using MageRide.Contract.Tests.Runtime;
using MageRide.Shared.Auth;

namespace MageRide.Security.Tests.Rbac;

/// <summary>
/// C127's definition-of-done item: <b>an automated RBAC probe finds no endpoint reachable without
/// an explicit permission</b> (AL-06, ADD §12.3).
///
/// <para>
/// The probe reads all twenty-four services' composed route tables — 440-odd endpoints — and asks
/// three questions of every one of them. First, is anything reachable with no credential
/// requirement at all? Second, does every endpoint that opts out of authentication have a reviewed
/// entry in <see cref="AnonymousSurface"/> naming what a caller must present instead? Third, of the
/// endpoints that do require a caller, how many name a permission rather than settling for "any
/// authenticated user"?
/// </para>
///
/// <para>
/// <b>The third question is a ratchet, not a pass/fail.</b> 141 endpoints today rely on the kernel
/// fallback plus an ownership check inside the handler — <c>GET /v1/rides/{rideId}</c> cannot be
/// expressed as a URD §2.3 cell, because §2.3 says which *roles* may read rides and the handler is
/// what knows whether ride 7 is yours. Demanding a feature policy on those would be demanding the
/// wrong control. What must not happen is the number growing quietly, so
/// <see cref="AuthenticatedOnlyLedger"/> pins it and the suite fails in both directions.
/// </para>
/// </summary>
public sealed class RbacProbeTests
{
    [Fact]
    public void The_probe_covers_every_service_in_the_fleet()
    {
        // The denominator, asserted before anything is asserted about it. A service that stopped
        // composing would otherwise contribute zero endpoints and read as zero findings.
        var composed = EndpointInventory.Services;
        var expected = ServiceCatalog.All.Select(static service => service.Document).Order(StringComparer.Ordinal);

        Assert.Equal(expected, composed);

        var endpoints = EndpointInventory.All;
        Assert.True(
            endpoints.Count >= 400,
            $"The probe read only {endpoints.Count} endpoints across {composed.Count} services. The fleet "
            + "has had more than four hundred since C118; a collapse this large means a service composed "
            + "without its route table rather than that endpoints were deleted.");

        var empty = composed
            .Where(service => !endpoints.Any(endpoint => endpoint.Service == service))
            .ToList();

        Assert.True(
            empty.Count == 0,
            $"{empty.Count} service(s) contributed no endpoints at all, which is a composition failure "
            + $"rather than a security posture: {string.Join(", ", empty)}");
    }

    [Fact]
    public void Every_service_registers_the_deny_by_default_fallback_policy()
    {
        // Half of AL-06 (the other half is RequireFeature). Without it an endpoint that names no
        // policy is public, and *every* AuthenticatedOnly classification below would be a lie.
        var missing = EndpointInventory.Services
            .Where(static service => !EndpointInventory.HasDenyByDefaultFallback(service))
            .Where(static service => !NoFallbackByDesign.ContainsKey(service))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"{missing.Count} service(s) have no fallback policy demanding an authenticated caller. Every "
            + "endpoint on them that does not name a policy of its own is anonymous:\n  "
            + string.Join("\n  ", missing));

        var recovered = NoFallbackByDesign.Keys
            .Where(EndpointInventory.HasDenyByDefaultFallback)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            recovered.Count == 0,
            $"{recovered.Count} service(s) now have the fallback policy and are still excused from it. Delete "
            + $"them from `NoFallbackByDesign`: {string.Join(", ", recovered)}");
    }

    [Theory]
    [MemberData(nameof(ServicesWithNoFallback))]
    public void A_service_that_cleared_the_fallback_serves_nothing_but_operational_and_internal_routes(string service)
    {
        // The compensating control, asserted rather than believed. Clearing the fallback is
        // defensible exactly while every route on the service is one a reviewer has already
        // classified — the kernel's health probes, or the key-gated internal plane. The moment a
        // twenty-fifth route lands here it is public, and this is what says so.
        var exposed = EndpointInventory.All
            .Where(endpoint => endpoint.Service == service)
            .Where(static endpoint => !AnonymousSurface.KernelOperationalRoutes.Contains(endpoint.Route))
            .Where(static endpoint =>
                !endpoint.Route.Split(' ', 2)[^1].StartsWith(AnonymousSurface.InternalPrefix, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            exposed.Count == 0,
            $"{service} cleared the deny-by-default fallback policy ({NoFallbackByDesign[service]}), so an "
            + "endpoint that names no policy of its own is reachable by anybody who can address it. These "
            + $"route(s) are neither a kernel health probe nor on the key-gated internal plane:\n  "
            + string.Join("\n  ", exposed));
    }

    public static TheoryData<string> ServicesWithNoFallback()
    {
        var data = new TheoryData<string>();
        foreach (var service in NoFallbackByDesign.Keys.Order(StringComparer.Ordinal))
        {
            data.Add(service);
        }

        return data;
    }

    /// <summary>
    /// Services that clear the kernel's fallback policy on purpose, and what makes that safe.
    /// </summary>
    /// <remarks>
    /// A fallback of <c>RequireAuthenticatedUser</c> applies to requests matching <b>no</b> endpoint
    /// as well as to endpoints that name nothing — so on a service with no authentication scheme
    /// registered it can never be satisfied, and every unknown path answers <c>500 "Unable to find
    /// the required 'IAuthenticationService'"</c> instead of <c>404</c>. Clearing it is the fix.
    /// The theory above is the price: nothing but health probes and the internal plane may be
    /// mapped on such a service.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> NoFallbackByDesign =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ocr"] =
                "no user-facing surface and no bearer on its plane, so no authentication scheme is "
                + "registered and the fallback could never be satisfied — an unknown path would 500",
        };

    [Fact]
    public void No_endpoint_is_reachable_with_no_credential_requirement()
    {
        // The headline assertion. `Guard.Open` means the endpoint carries no authorization
        // metadata AND its service has no deny-by-default fallback — reachable by anyone who can
        // address it.
        var open = EndpointInventory.All
            .Where(static endpoint => endpoint.Guard == Guard.Open)
            .ToList();

        Assert.True(
            open.Count == 0,
            $"{open.Count} endpoint(s) are reachable with no credential requirement of any kind (AL-06):\n  "
            + string.Join("\n  ", open));
    }

    [Fact]
    public void Every_anonymous_endpoint_has_a_reviewed_compensating_credential()
    {
        // An endpoint filter leaves no metadata, so `AllowAnonymous` alone cannot be told from
        // `AllowAnonymous` + InternalKeyFilter by reflection. This is the review, held as data.
        var unreviewed = EndpointInventory.All
            .Where(static endpoint => endpoint.Guard == Guard.Anonymous)
            .Where(static endpoint => AnonymousSurface.Find(endpoint) is null)
            .ToList();

        Assert.True(
            unreviewed.Count == 0,
            $"{unreviewed.Count} endpoint(s) opt out of authentication and no reviewer has written down what "
            + "authenticates them instead. Read the handler, then add an entry to `AnonymousSurface.Reviewed` "
            + "naming the credential — or give the endpoint a policy:\n  "
            + string.Join("\n  ", unreviewed.Select(static endpoint => endpoint.Key)));
    }

    [Fact]
    public void The_reviewed_anonymous_surface_is_still_the_surface_that_was_reviewed()
    {
        // The other direction of the ratchet. An entry for a route that no longer exists is an
        // exemption nobody is checking, and the next route to land on that path inherits it.
        var live = EndpointInventory.All
            .Where(static endpoint => endpoint.Guard == Guard.Anonymous)
            .Select(static endpoint => endpoint.Key)
            .ToHashSet(StringComparer.Ordinal);

        var stale = AnonymousSurface.Reviewed
            .Select(static entry => entry.Key)
            .Where(key => !live.Contains(key))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            stale.Count == 0,
            $"{stale.Count} entr(y/ies) in `AnonymousSurface.Reviewed` name a route that is no longer served "
            + "anonymously. Delete them — a stale exemption is one the next endpoint on that path inherits:\n  "
            + string.Join("\n  ", stale));
    }

    [Fact]
    public void No_anonymous_endpoint_outside_the_reviewed_edge_set_is_reachable_from_the_internet()
    {
        // `EdgeReachable: false` is a claim about the gateway's route table, and the gateway's route
        // table is a file in this repository. Asserting the claim here means the ledger cannot say
        // "internal only" about something the edge went on to publish.
        var contradicted = EndpointInventory.All
            .Where(static endpoint => endpoint.Guard == Guard.Anonymous)
            .Select(static endpoint => (endpoint, review: AnonymousSurface.Find(endpoint)))
            .Where(static pair => pair.review is { EdgeReachable: false })
            .Where(static pair => GatewayRouteTable.RoutesFromTheInternet(pair.endpoint.Route))
            .Select(static pair => $"{pair.endpoint.Key} — the ledger says internal-only")
            .ToList();

        Assert.True(
            contradicted.Count == 0,
            $"{contradicted.Count} endpoint(s) are described as unreachable from the public edge and are "
            + "routed there by `gateway-routes.json`:\n  " + string.Join("\n  ", contradicted));
    }

    [Fact]
    public void The_endpoints_that_name_no_permission_are_the_ones_that_were_signed_off()
    {
        var actual = EndpointInventory.All
            .Where(static endpoint => endpoint.Guard == Guard.AuthenticatedOnly)
            .Select(static endpoint => endpoint.Key)
            .ToHashSet(StringComparer.Ordinal);

        var recorded = AuthenticatedOnlyLedger.Count;

        Assert.True(
            actual.Count <= recorded,
            $"{actual.Count} endpoints require only an authenticated caller; {recorded} were reviewed and "
            + "signed off in C127. The count has GROWN, which means a new privileged endpoint settled for "
            + "\"any logged-in user\". Either name its URD §2.3 cell with RequireFeature, or raise the "
            + "ledger count in `AuthenticatedOnlyLedger` with a note saying why the check belongs in the "
            + "handler.");

        // Shrinking is the point of the ratchet, but it must be deliberate: a drop means either
        // somebody tightened an endpoint (good — record it) or a service stopped composing (bad).
        Assert.True(
            actual.Count >= recorded - AuthenticatedOnlyLedger.Tolerance,
            $"Only {actual.Count} endpoints rely on the fallback policy, against {recorded} recorded. If "
            + "endpoints were tightened, lower the count in `AuthenticatedOnlyLedger` in the same change. "
            + "If a service failed to compose, this is that failure wearing a green tick.");
    }

    [Fact]
    public void Every_operator_endpoint_on_the_back_office_names_a_feature_area_or_a_role()
    {
        // The back-office surface: D-35 writes an audit row for every mutation here, and URD §2.3
        // has a matrix row for every area it covers. "Authenticated" is never the right answer on
        // it — every passenger on the platform holds a valid bearer.
        var weak = EndpointInventory.All
            .Where(static endpoint => endpoint.Service is "admin-bff")
            .Where(static endpoint => endpoint.Route.Contains(" /v1/admin/", StringComparison.Ordinal))
            .Where(static endpoint => endpoint.Guard is Guard.AuthenticatedOnly or Guard.Open)
            .Select(static endpoint => $"{endpoint.Key} — {endpoint.Detail}")
            .ToList();

        Assert.True(
            weak.Count == 0,
            $"{weak.Count} operator endpoint(s) admit any authenticated caller. Every passenger holds a "
            + "valid bearer; URD §2.3 is what says who may do these things:\n  " + string.Join("\n  ", weak));
    }

    [Fact]
    public void The_only_back_office_routes_outside_the_matrix_are_the_data_subjects_own_rights()
    {
        // `/v1/pdpa/**` is the one family on admin-bff that is authenticated and NOT feature-gated,
        // and the reason is that URD §2.3 has no cell for it: the End-user-account-management row
        // gives PAX and DRV ➖ because it is about operating on *other people's* accounts. Gating a
        // statutory own-account right on it would refuse every data subject their own data. The
        // control is in the handler — the request is scoped by the `sub` claim, and one that is not
        // yours answers 404 rather than 403 so the route is not an oracle over live erasure ids.
        //
        // What this asserts is the *boundary*: that the exception has not spread. A fourth
        // un-gated family on this service fails here, and `AdminBffApplication.GuardTheSurface`
        // refuses to start on a fourth prefix.
        var ungated = EndpointInventory.All
            .Where(static endpoint => endpoint.Service == "admin-bff")
            .Where(static endpoint => endpoint.Guard is not (Guard.Feature or Guard.Role))
            .Where(static endpoint => !AnonymousSurface.KernelOperationalRoutes.Contains(endpoint.Route))
            .Select(static endpoint => endpoint.Route)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            ["GET /v1/pdpa/{}", "POST /v1/pdpa/erasure", "POST /v1/pdpa/export"],
            ungated);
    }

    [Fact]
    public void Every_feature_policy_names_an_area_the_matrix_actually_has()
    {
        // FeatureAuthorizationHandler denies an unknown area rather than throwing, which is the
        // safe direction and also a silent one: the endpoint would answer 403 for everybody
        // including the role that is supposed to hold it, and look like a permissions bug.
        var unknown = EndpointInventory.All
            .Where(static endpoint => endpoint.Guard == Guard.Feature)
            .SelectMany(static endpoint => endpoint.Detail
                .Split(" + ", StringSplitOptions.RemoveEmptyEntries)
                .Select(part => (endpoint, area: part.Split(':')[1])))
            .Where(static pair => FeatureAreas.Find(pair.area) is null)
            .Select(static pair => $"{pair.endpoint.Key} names feature area '{pair.area}'")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unknown.Count == 0,
            $"{unknown.Count} endpoint(s) gate on a feature area that is not one of the twenty-one URD §2.3 "
            + "rows. Deny-by-default makes that a 403 for every role, which reads as a permissions bug:\n  "
            + string.Join("\n  ", unknown));
    }
}

/// <summary>
/// How many endpoints are allowed to rely on the kernel fallback alone, and why that is not zero.
/// </summary>
/// <remarks>
/// <para>
/// URD §2.3 answers "may this role do this kind of thing". It cannot answer "is this row yours",
/// and most of the app surface is the second question: <c>GET /v1/rides/{rideId}</c>,
/// <c>PUT /v1/me/saved-addresses/{id}</c>, <c>GET /v1/wallet/{userId}</c>. For those the control
/// is an ownership check against the <c>sub</c> claim inside the handler, which the owning
/// service's own suite drives. Requiring a feature policy on them would move the check to a layer
/// that does not know the answer.
/// </para>
/// <para>
/// So the number is a ratchet rather than a target. It was 141 at C127 sign-off across twenty-four
/// services; the review walked the admin and fleet surfaces specifically, which is where a missing
/// permission is exploitable rather than merely untidy, and those are asserted separately above.
/// </para>
/// </remarks>
internal static class AuthenticatedOnlyLedger
{
    /// <summary>The count at C127 sign-off, 2026-08-12.</summary>
    public const int Count = 141;

    /// <summary>
    /// How far below <see cref="Count"/> the suite tolerates before it treats a drop as a
    /// composition failure rather than as tightening. Small on purpose: tightening five endpoints
    /// is a change worth recording, and losing a service is worth failing on.
    /// </summary>
    public const int Tolerance = 8;
}
