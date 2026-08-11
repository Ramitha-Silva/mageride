using MageRide.AdminBff;
using MageRide.ApiGateway;
using MageRide.Content;
using MageRide.Dispatch;
using MageRide.Fare;
using MageRide.Fleet;
using MageRide.FleetBilling;
using MageRide.HotPath.Host;
using MageRide.Iam;
using MageRide.Notification;
using MageRide.Ocr;
using MageRide.Payout;
using MageRide.Provisioning;
using MageRide.PublicBff;
using MageRide.Query;
using MageRide.Registry;
using MageRide.Reputation;
using MageRide.Ride;
using MageRide.Safety;
using MageRide.Subscriptions;
using MageRide.Support;
using MageRide.Transit;
using MageRide.TripState;
using MageRide.Wallet;

namespace MageRide.AppServices;

/// <summary>
/// What lives inside the replica's Container 7, and which of the gateway's clusters this container
/// is <b>not</b> responsible for.
/// </summary>
/// <remarks>
/// <para>
/// A type rather than a list inside <c>Program.cs</c> so it can be asserted against
/// <c>gateway-routes.json</c>. Every name in <see cref="Services"/> is also a cluster id, and the
/// host points that cluster at the loopback port the service is listening on — so a service renamed
/// on one side and not the other is a 502 in production, discoverable only by trying every route.
/// <c>Container7Tests</c> makes it a failing test instead.
/// </para>
/// </remarks>
public static class Container7
{
    /// <summary>The 22 domain services, in start order, each with its own real entry point.</summary>
    public static IReadOnlyList<CoLocatedService> Services { get; } =
    [
        new("iam-svc", IamApplication.Build),
        new("registry-svc", RegistryApplication.Build),
        new("provisioning-svc", ProvisioningApplication.Build),
        new("query-svc", QueryApplication.Build),
        new("trip-state-svc", TripStateApplication.Build),
        new("ride-svc", RideApplication.Build),
        new("dispatch-svc", DispatchApplication.Build),
        new("fare-svc", FareApplication.Build),
        new("subscription-svc", SubscriptionApplication.Build),
        new("wallet-svc", WalletApplication.Build),
        new("notification-svc", NotificationApplication.Build),
        new("safety-svc", SafetyApplication.Build),
        new("reputation-svc", ReputationApplication.Build),
        new("content-svc", ContentApplication.Build),
        new("support-svc", SupportApplication.Build),
        new("fleet-svc", FleetApplication.Build),
        new("admin-bff", AdminBffApplication.Build),
        new("ocr-svc", OcrApplication.Build),
        new("transit-svc", TransitApplication.Build),
        new("public-bff", PublicBffApplication.Build),
        new("payout-svc", PayoutApplication.Build),
        new("fleet-billing-svc", FleetBillingApplication.Build),
    ];

    /// <summary>The gateway, which binds the one port this container publishes.</summary>
    public static CoLocatedService Gateway { get; } = new("api-gateway", GatewayApplication.Build);

    /// <summary>The port the first service in <see cref="Services"/> binds.</summary>
    public const int FirstServicePort = 5101;

    /// <summary>The port the gateway publishes — the spec's Container 7 table.</summary>
    public const int GatewayPort = 5000;

    /// <summary>
    /// Gateway clusters that are deliberately NOT co-located here, and must keep the address the
    /// environment gives them.
    /// </summary>
    /// <remarks>
    /// <c>fanout-svc</c> is Container 8 and <c>voip-svc</c> the optional Container 11, each its own
    /// process on its own host name. <c>fleet-health-svc</c> is inside Container 6 — the same
    /// machine, a different process. Pointing any of the three at a loopback port in <i>this</i>
    /// process would give the gateway an address nothing is listening on, and the symptom would be a
    /// 502 on exactly the routes a smoke test is least likely to cover.
    /// <c>ocr-svc</c> is not here: it is co-located in this container but has no cluster at all,
    /// because it is queue-driven and reached directly by fleet-svc through <c>Fleet:OcrBaseUrl</c>.
    /// </remarks>
    public static IReadOnlySet<string> ClustersElsewhere { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "fanout-svc", "fleet-health-svc", "voip-svc" };
}
