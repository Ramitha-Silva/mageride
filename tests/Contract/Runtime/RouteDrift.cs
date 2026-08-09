namespace MageRide.Contract.Tests.Runtime;

/// <summary>
/// The drift this suite found on the day it landed, written down.
///
/// <para>
/// <b>Why a list exists at all.</b> C118's fence is "the contract is the spec-derived document in
/// `backend/contracts/`. If an implementation disagrees, the implementation is wrong unless a
/// micro-change-set says otherwise." Every entry below is an implementation that disagrees, and
/// fixing them touches five services and two specs — not something a test suite may do on its way
/// past. So each is recorded, named, explained, and <b>ratcheted</b>: the suite fails if the list
/// grows, and it fails if an entry is fixed and left here. It is a ledger of debt, not a
/// suppression, and every entry is reproduced in the C118 handoff with the component that owns it.
/// </para>
/// </summary>
internal static class RouteDrift
{
    /// <summary>
    /// Operations a contract declares that no service in the fleet maps.
    /// </summary>
    /// <remarks>
    /// <para>The direction that matters most: a client generated from the contract calls these and
    /// gets a routing 404. Keyed by <c>operationId</c>, which is what a client's method is named
    /// after.</para>
    ///
    /// <list type="bullet">
    /// <item>
    /// <b><c>bindFleetTracker</c></b> — `POST /v1/fleets/{fleetId}/trackers/bind`, and the worst of
    /// the five, because it is not a missing route but a <b>boot failure</b>. fleet-svc maps it only
    /// when `Fleet:ProvisioningBaseUrl` is configured, and mapping it throws
    /// `InvalidOperationException: BindAsync method found on ITrackerBindingService with incorrect
    /// format`: the handler takes `ITrackerBindingService` with no `[FromServices]`, and that
    /// interface has an instance `BindAsync`, which minimal APIs read as a custom parameter binder
    /// and refuse. <b>Any deployment that configures the provisioning upstream — which production
    /// must — has a fleet-svc that does not start.</b> Nothing caught it because every existing
    /// harness leaves the setting unset, so the route is never mapped in a test. One attribute fixes
    /// it; the fix belongs in a change that also has fleet-svc's own suite configure the upstream.
    /// </item>
    /// <item>
    /// <b><c>listRideHistory</c></b>, <b><c>disputeRide</c></b> — `ride.yaml` declares
    /// `GET /v1/rides/history` and `POST /v1/rides/{rideId}/dispute`; ride-svc maps neither, and no
    /// other service picks them up. The history read is a query-svc-shaped operation living in
    /// ride-svc's document; the dispute has a sibling in fare-svc (`POST /v1/fare/pay/driver-qr/dispute`,
    /// AL-47), which may be what superseded it.
    /// </item>
    /// <item>
    /// <b><c>bindVehicleDevice</c></b> — `registry.yaml` declares `POST /v1/vehicles/{vehicleId}/device`
    /// and registry-svc serves `POST /v1/vehicles/{vehicleId}/select-live` instead. A rename that
    /// reached the service and not the document, or two operations one of which was never built.
    /// </item>
    /// <item>
    /// <b><c>importGtfsFeed</c></b> — `POST /v1/admin/transit/gtfs-import`, which AL-54 <b>superseded</b>
    /// with the versioned `/admin/transit/gtfs/*` set ("raw `gtfs-import` superseded"). transit-svc
    /// has already removed it. C007's own rule is that "a superseded endpoint is deleted, not
    /// deprecated", so the contract is the side that is behind here.
    /// </item>
    /// </list>
    /// </remarks>
    public static readonly IReadOnlySet<string> UnmappedOperations = new HashSet<string>(StringComparer.Ordinal)
    {
        "bindFleetTracker",
        "listRideHistory",
        "disputeRide",
        "bindVehicleDevice",
        "importGtfsFeed",
    };

    /// <summary>
    /// Routes a service serves that no contract document declares, as <c>"{document} {METHOD} {path}"</c>.
    /// </summary>
    /// <remarks>
    /// <para>The quieter direction, and not the harmless one: an endpoint with no contract is an
    /// endpoint no client can be generated for, no lint has checked the error codes of, and no
    /// reviewer has read the security declaration of.</para>
    ///
    /// <list type="bullet">
    /// <item>
    /// <b>admin-bff's four GTFS routes</b> — `/v1/admin/transit/gtfs/{…}` on four verbs. AL-54's
    /// versioned upload/preview/activate/rollback set is declared in `transit.yaml`, and the C110
    /// handoff records that the gateway routes `/v1/admin/transit/**` <b>past</b> admin-bff to
    /// transit-svc at Order 20 — so admin-bff proxies a family it is not on the path for. Either the
    /// proxy is dead code or `admin-bff.yaml` is missing four operations; the gateway table says the
    /// first.
    /// </item>
    /// <item>
    /// <b><c>registry POST /v1/vehicles/{}/select-live</c></b> — the other half of `bindVehicleDevice`
    /// above. One of the two names is right and the two documents disagree about which.
    /// </item>
    /// <item>
    /// <b><c>registry POST /v1/dev/vehicles/{}/approve</c></b> — a development shortcut, mapped
    /// because this suite composes in the Development environment (as every harness does). It is
    /// listed rather than filtered by prefix: a `/v1/dev/**` route that reached a production image
    /// would be an approval endpoint with no contract, and a filter would hide exactly that.
    /// </item>
    /// <item>
    /// <b><c>subscription POST /v1/mode-b/pay/onepay/webhook</c></b> — <b>AL-59 removed OnePay from
    /// Mode B</b> because "it would have landed subscriber money in MageRide's account", and
    /// `subscription.yaml` no longer declares the route. subscription-svc still serves it. A live,
    /// undocumented payment webhook on a rail the platform decided not to have.
    /// </item>
    /// </list>
    /// </remarks>
    public static readonly IReadOnlySet<string> UndocumentedRoutes = new HashSet<string>(StringComparer.Ordinal)
    {
        "admin-bff DELETE /v1/admin/transit/gtfs/{}",
        "admin-bff GET /v1/admin/transit/gtfs/{}",
        "admin-bff POST /v1/admin/transit/gtfs/{}",
        "admin-bff PUT /v1/admin/transit/gtfs/{}",
        "registry POST /v1/dev/vehicles/{}/approve",
        "registry POST /v1/vehicles/{}/select-live",
        "subscription POST /v1/mode-b/pay/onepay/webhook",
    };
}
