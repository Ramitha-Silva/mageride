using System.Net;
using MageRide.ApiGateway.Http;
using MageRide.ApiGateway.Tests.Infrastructure;

namespace MageRide.ApiGateway.Tests;

/// <summary>
/// DoD: "the route table resolves every service named in <c>backend/contracts/</c>".
/// <para>
/// Every operation in every contract is driven through a running gateway and the cluster that
/// served it is read back off <c>X-MageRide-Upstream</c>. The expected cluster comes from the
/// contract's own file name, so this cannot drift: an endpoint added to a contract without a
/// matching route fails here, and so does one routed to the wrong service.
/// </para>
/// </summary>
public sealed class RouteTableTests : IAsyncLifetime
{
    private GatewayHarness _gateway = null!;

    public async ValueTask InitializeAsync() => _gateway = await GatewayHarness.StartAsync();

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    [Fact]
    public void Contracts_are_discoverable()
    {
        // A silent zero here would make every other assertion in this class vacuously true.
        Assert.True(ContractCatalog.Operations.Count > 200,
            $"Expected the full contract set; found {ContractCatalog.Operations.Count} operations in {ContractCatalog.ContractsDirectory}.");
    }

    [Fact]
    public async Task Every_public_contract_operation_routes_to_its_own_service()
    {
        var failures = new List<string>();

        foreach (var operation in ContractCatalog.Operations)
        {
            if (operation.Cluster is null || operation.IsInternalPlane)
            {
                continue;
            }

            using var request = new HttpRequestMessage(new HttpMethod(operation.Method), operation.ConcretePath);
            using var response = await _gateway.Client.SendAsync(request);

            var upstream = response.Headers.TryGetValues(GatewayTransforms.UpstreamHeaderName, out var values)
                ? values.FirstOrDefault()
                : null;

            if (upstream != operation.Cluster)
            {
                failures.Add(
                    $"{operation.Method,-6} {operation.Template} ({operation.OperationId}) -> {upstream ?? $"unrouted, {(int)response.StatusCode}"}, expected {operation.Cluster}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task Every_declared_route_is_reachable_from_some_contract_path()
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);

        foreach (var operation in ContractCatalog.Operations)
        {
            if (operation.Cluster is null || operation.IsInternalPlane)
            {
                continue;
            }

            using var request = new HttpRequestMessage(new HttpMethod(operation.Method), operation.ConcretePath);
            using var response = await _gateway.Client.SendAsync(request);

            if (response.Headers.TryGetValues(GatewayTransforms.UpstreamHeaderName, out var values))
            {
                reached.Add(values.First());
            }
        }

        // /hubs is a realtime surface with no OpenAPI document (C007 handoff (j)); it is proved by
        // SignalRPassthroughTests instead.
        var expected = GatewayHarness.ClusterIds.Where(static c => c != "fanout-svc").ToArray();
        var unreachable = expected.Except(reached, StringComparer.Ordinal).ToArray();

        Assert.True(unreachable.Length == 0,
            "No contract path resolves to these clusters, so nothing routes to them: " + string.Join(", ", unreachable));
    }

    /// <summary>
    /// Δ C127. Every operation the contracts put on the mTLS plane is refused at the edge —
    /// <b>read from its declared <c>security</c>, not from its path</b>.
    /// </summary>
    /// <remarks>
    /// The distinction is the whole finding. Keying on <c>/v1/internal/**</c> covered forty-six of
    /// the forty-nine mTLS operations and published the other three (<c>calculateFinalFare</c>,
    /// <c>renderNotificationTemplate</c>, <c>lookupUserByPhone</c>), each of which then had a shared
    /// secret as its only control. `Gateway:BlockedPathPrefixes` names all three; this is what fails
    /// if a fourth is written, because the contract says which plane it is on and the path does not.
    /// </remarks>
    [Fact]
    public async Task Every_operation_the_contract_puts_on_the_mtls_plane_is_refused_at_the_edge()
    {
        var internalOperations = ContractCatalog.Operations
            .Where(static o => o.IsInternalPlane)
            .ToArray();

        Assert.NotEmpty(internalOperations);

        // The three that motivated this, named so a change that quietly stopped classifying them
        // fails here rather than passing over a smaller set.
        Assert.Contains(internalOperations, static o => o.OperationId == "calculateFinalFare");
        Assert.Contains(internalOperations, static o => o.OperationId == "renderNotificationTemplate");
        Assert.Contains(internalOperations, static o => o.OperationId == "lookupUserByPhone");

        foreach (var operation in internalOperations)
        {
            using var request = new HttpRequestMessage(new HttpMethod(operation.Method), operation.ConcretePath);
            using var response = await _gateway.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.False(response.Headers.Contains(GatewayTransforms.UpstreamHeaderName),
                $"{operation.Method} {operation.Template} was forwarded. The contract declares it "
                + "`security: [{ mtls: [] }]`, so the edge must refuse it (D3' §0) — add its path to "
                + "Gateway:BlockedPathPrefixes.");

            var problem = await ProblemDocument.ReadAsync(response);
            Assert.Equal("not-found", problem.Code);
        }
    }

    [Fact]
    public async Task Version_check_is_served_by_the_gateway_itself()
    {
        using var response = await _gateway.Client.GetAsync("/v1/version/check");

        Assert.False(response.Headers.Contains(GatewayTransforms.UpstreamHeaderName));

        // No query string, so the local endpoint answers with its own validation problem — which
        // is the proof that it ran here rather than being proxied to a version-check service.
        var problem = await ProblemDocument.ReadAsync(response);
        Assert.Equal("validation-failed", problem.Code);
    }

    [Theory]
    // The six literal-vs-template overlaps C007's handoff flagged for this component, two of which
    // cross a service boundary. A prefix-only rule on /v1/rides sends the first three to ride-svc.
    [InlineData("GET", "/v1/rides/job-board", "dispatch-svc")]
    [InlineData("POST", "/v1/rides/job-board/01JZ/intent", "dispatch-svc")]
    [InlineData("GET", "/v1/rides/scheduled/01JZ", "dispatch-svc")]
    [InlineData("POST", "/v1/rides/schedule", "dispatch-svc")]
    [InlineData("DELETE", "/v1/rides/schedule/01JZ", "dispatch-svc")]
    [InlineData("GET", "/v1/rides/history", "ride-svc")]
    [InlineData("GET", "/v1/rides/01JZ", "ride-svc")]
    [InlineData("GET", "/v1/rides/01JZ/state", "ride-svc")]
    [InlineData("GET", "/v1/vehicles/mine", "registry-svc")]
    [InlineData("GET", "/v1/vehicles/01JZ", "registry-svc")]
    [InlineData("GET", "/v1/mode-b/subscriptions/01JZ", "subscription-svc")]
    [InlineData("GET", "/v1/mode-b/01JZ/subscribers", "subscription-svc")]
    // /v1/drivers is split three ways and has no catch-all owner.
    [InlineData("POST", "/v1/drivers/profile", "registry-svc")]
    [InlineData("GET", "/v1/drivers/01JZ/level", "dispatch-svc")]
    [InlineData("POST", "/v1/drivers/01JZ/block", "safety-svc")]
    // /v1/admin belongs to admin-bff except for the sub-trees other services own.
    [InlineData("POST", "/v1/admin/auth/login", "iam-svc")]
    [InlineData("PUT", "/v1/admin/drivers/level-config", "dispatch-svc")]
    [InlineData("POST", "/v1/admin/drivers/01JZ/level/restore", "reputation-svc")]
    [InlineData("POST", "/v1/admin/drivers/01JZ/suspend", "admin-bff")]
    [InlineData("POST", "/v1/admin/drivers/wallet/01JZ/reverse-fee", "admin-bff")]
    [InlineData("GET", "/v1/admin/drivers", "admin-bff")]
    [InlineData("POST", "/v1/admin/fare/refund", "fare-svc")]
    [InlineData("PUT", "/v1/admin/fares/tariffs", "admin-bff")]
    [InlineData("PUT", "/v1/admin/fees/rates", "subscription-svc")]
    [InlineData("PUT", "/v1/admin/voucher-discount-tiers", "subscription-svc")]
    [InlineData("PUT", "/v1/wallet/admin/voucher-discount-tiers", "wallet-svc")]
    [InlineData("GET", "/v1/config/cities", "content-svc")]
    [InlineData("GET", "/v1/admin/config/cities", "admin-bff")]
    [InlineData("PUT", "/v1/admin/content/ride_offer", "content-svc")]
    [InlineData("POST", "/v1/admin/transit/gtfs/uploads", "transit-svc")]
    [InlineData("GET", "/v1/admin/reputation/flags", "reputation-svc")]
    [InlineData("PUT", "/v1/admin/dispatch/directional-config", "dispatch-svc")]
    // Trackers: bulk provisioning lives under the fleet path but belongs to provisioning-svc.
    [InlineData("POST", "/v1/fleets/01JZ/trackers/bulk", "provisioning-svc")]
    [InlineData("GET", "/v1/fleets/01JZ/trackers/bulk/01JY", "provisioning-svc")]
    [InlineData("POST", "/v1/fleets/01JZ/trackers/bind", "fleet-svc")]
    // Health: the same split again (C044). D3' lists the route in the fleet-svc table and attributes it
    // to fleet-health-svc in the same line, so the contract lives in fleet-health.yaml.
    [InlineData("GET", "/v1/fleets/01JZ/health", "fleet-health-svc")]
    // Billing: the same split a third time (C060). ADD §6 gives the fleet wallet and the monthly
    // per-Mode-B-vehicle invoicing to fleet-billing-svc; D3' lists both routes under fleet-svc.
    [InlineData("GET", "/v1/fleets/01JZ/billing", "fleet-billing-svc")]
    [InlineData("GET", "/v1/fleets/01JZ/billing/01JY", "fleet-billing-svc")]
    [InlineData("POST", "/v1/fleets/01JZ/wallet/topup", "fleet-billing-svc")]
    [InlineData("POST", "/v1/fleet-billing/topup/onepay/webhook", "fleet-billing-svc")]
    [InlineData("GET", "/v1/fleets/01JZ/map", "fleet-svc")]
    // /v1/geo is split between the geocoder proxy and the GTFS link parser.
    [InlineData("GET", "/v1/geo/search", "query-svc")]
    [InlineData("GET", "/v1/geo/reverse", "query-svc")]
    [InlineData("POST", "/v1/geo/parse-maps-link", "transit-svc")]
    public async Task Overlapping_prefixes_resolve_to_the_owning_service(string method, string path, string cluster)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await _gateway.Client.SendAsync(request);

        Assert.True(response.Headers.TryGetValues(GatewayTransforms.UpstreamHeaderName, out var values),
            $"{method} {path} was not routed ({(int)response.StatusCode}).");
        Assert.Equal(cluster, values!.First());
    }
}
