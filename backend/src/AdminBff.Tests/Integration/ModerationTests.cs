using System.Net;
using MageRide.AdminBff.Tests.Infrastructure;
using MageRide.Shared.Auth;
using MageRide.TestKit;

namespace MageRide.AdminBff.Tests.Integration;

/// <summary>
/// The moderation half of the surface: suspensions (US-14.3), the report queue and its decision
/// (US-12.6), and the support queue (US-16.3).
/// </summary>
[Collection(AdminBffCollection.Name)]
public sealed class ModerationTests(PostgresFixture postgres)
{
    /// <summary>
    /// Suspending a vehicle takes it out of dispatch <em>and</em> off the live map, in one
    /// transaction.
    /// </summary>
    /// <remarks>
    /// The contract's "removes it from dispatch and the public map immediately" is two facts about
    /// two tables: <c>dispatch_state</c> is what dispatch-svc's candidate query excludes, and the
    /// live tracking session is what the map draws. Asserting only the first would leave a suspended
    /// bus still moving on every passenger's screen.
    /// </remarks>
    [Fact]
    public async Task Suspending_a_vehicle_stops_dispatch_and_ends_its_live_session()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var admin = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);
        var (driverId, vehicleId) = await harness.Seed.DriverWithVehicleAsync();
        var sessionId = await harness.Seed.LiveSessionAsync(driverId, vehicleId);
        await harness.Seed.PresenceAsync(driverId, vehicleId);

        using var response = await harness.SendAsync(
            HttpMethod.Post,
            $"/v1/admin/vehicles/{vehicleId:D}/suspend",
            harness.Tokens.Admin(admin),
            new { reason = "Unsafe vehicle" });

        using var payload = await harness.ReadJsonAsync(response);

        Assert.Equal("SUSPENDED", payload.RootElement.GetProperty("status").GetString());
        Assert.Equal("DISPATCH_SUSPENDED", await harness.Seed.VehicleDispatchStateAsync(vehicleId));
        Assert.Equal("COMPLETED", await harness.Seed.SessionStateAsync(sessionId));
        Assert.Equal("OFFLINE", await harness.Seed.PresenceStateAsync(driverId));
    }

    /// <summary>
    /// Suspending twice is a 200 both times, and records both attempts.
    /// </summary>
    /// <remarks>
    /// A 409 on the second would make the Admin Portal's button fail on a double click. The second
    /// row's before and after agree, which is the honest record: an admin performed the action and
    /// it changed nothing.
    /// </remarks>
    [Fact]
    public async Task Suspending_an_already_suspended_vehicle_is_idempotent_and_still_audited()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var admin = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);
        var (_, vehicleId) = await harness.Seed.DriverWithVehicleAsync();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await harness.SendAsync(
                HttpMethod.Post,
                $"/v1/admin/vehicles/{vehicleId:D}/suspend",
                harness.Tokens.Admin(admin),
                new { reason = "Unsafe vehicle" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(2, (await harness.Seed.AuditRowsAsync(vehicleId)).Count);
    }

    /// <summary>
    /// Suspending a driver blocks the account, ends the session and signs the handset out — and
    /// leaves any ride already in flight alone.
    /// </summary>
    [Fact]
    public async Task Suspending_a_driver_blocks_the_account_and_ends_the_session()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var admin = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);
        var (driverId, vehicleId) = await harness.Seed.DriverWithVehicleAsync();
        var sessionId = await harness.Seed.LiveSessionAsync(driverId, vehicleId);
        await harness.Seed.PresenceAsync(driverId, vehicleId);

        using var response = await harness.SendAsync(
            HttpMethod.Post,
            $"/v1/admin/drivers/{driverId:D}/suspend",
            harness.Tokens.Admin(admin),
            new { reason = "Fraudulent trips" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(await harness.Seed.DriverIsBlockedAsync(driverId));
        Assert.Equal("COMPLETED", await harness.Seed.SessionStateAsync(sessionId));
        Assert.Equal("OFFLINE", await harness.Seed.PresenceStateAsync(driverId));
    }

    /// <summary>A suspension with no reason is refused before anything is written.</summary>
    [Fact]
    public async Task A_suspension_without_a_reason_is_refused()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var admin = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);
        var (_, vehicleId) = await harness.Seed.DriverWithVehicleAsync();

        using var response = await harness.SendAsync(
            HttpMethod.Post, $"/v1/admin/vehicles/{vehicleId:D}/suspend", harness.Tokens.Admin(admin), new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("ACTIVE", await harness.Seed.VehicleDispatchStateAsync(vehicleId));
    }

    /// <summary>
    /// The queue and the decision are forwarded to safety-svc, with the shared key and the
    /// operator's own identity.
    /// </summary>
    [Fact]
    public async Task The_report_decision_is_forwarded_to_the_service_that_owns_it()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var admin = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);

        using var queue = await harness.GetAsync("/v1/admin/reports/queue", harness.Tokens.Admin(admin));
        using var queuePayload = await harness.ReadJsonAsync(queue);

        Assert.Equal(
            SeedIds.Report,
            queuePayload.RootElement.GetProperty("items")[0].GetProperty("reportId").GetGuid());

        using var resolve = await harness.SendAsync(
            HttpMethod.Post,
            $"/v1/admin/reports/{SeedIds.Report:D}/resolve",
            harness.Tokens.Admin(admin),
            new { decision = "confirmed" });

        using var resolved = await harness.ReadJsonAsync(resolve);

        Assert.Equal(3, resolved.RootElement.GetProperty("confirmedCount").GetInt32());
        Assert.True(resolved.RootElement.GetProperty("vehicleDelisted").GetBoolean());

        var forwarded = harness.Upstream.Last("/reports/");

        // The internal plane's credential, not the operator's bearer: safety-svc has no bearer to
        // check, and the deciding admin travels on the body so a delisting stays appealable.
        Assert.Equal(StubUpstream.InternalKey, forwarded.InternalKey);
        Assert.Null(forwarded.Authorization);
        Assert.Contains(admin.ToString(), forwarded.Body, StringComparison.OrdinalIgnoreCase);

        // The operator's key reaches the service that owns the command log.
        Assert.NotNull(forwarded.IdempotencyKey);
    }

    /// <summary>A decision that is neither CONFIRMED nor DISMISSED never leaves the process.</summary>
    [Fact]
    public async Task An_unknown_report_decision_is_refused_locally()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var admin = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);
        var before = harness.Upstream.Calls.Count;

        using var response = await harness.SendAsync(
            HttpMethod.Post,
            $"/v1/admin/reports/{SeedIds.Report:D}/resolve",
            harness.Tokens.Admin(admin),
            new { decision = "MAYBE" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, harness.Upstream.Calls.Count);
    }

    /// <summary>
    /// An upstream 404 arrives at the operator as a 404, and an upstream outage as a 503.
    /// </summary>
    /// <remarks>
    /// The status crosses the boundary because "no such report" is something the operator can act
    /// on; the upstream's <c>type</c> URI and trace id do not, because they name another service's
    /// error registry.
    /// </remarks>
    [Fact]
    public async Task An_upstream_failure_keeps_its_meaning()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var admin = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);

        harness.Upstream.FailNext("/reports/", StatusCodes.Status404NotFound, "No such report.");

        using var missing = await harness.SendAsync(
            HttpMethod.Post,
            $"/v1/admin/reports/{Guid.CreateVersion7():D}/resolve",
            harness.Tokens.Admin(admin),
            new { decision = "DISMISSED" });

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);

        harness.Upstream.FailNext("/reports/", StatusCodes.Status500InternalServerError, "boom");

        using var broken = await harness.SendAsync(
            HttpMethod.Post,
            $"/v1/admin/reports/{SeedIds.Report:D}/resolve",
            harness.Tokens.Admin(admin),
            new { decision = "DISMISSED" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, broken.StatusCode);
    }

    /// <summary>An unconfigured upstream is a 503 on a route that is still mapped and still gated.</summary>
    [Fact]
    public async Task An_unconfigured_upstream_answers_503_rather_than_disappearing()
    {
        await using var harness = await AdminBffHarness.StartAsync(
            postgres,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["AdminBff:Upstreams:Support:BaseUrl"] = string.Empty,
            });

        var admin = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);

        using var allowed = await harness.GetAsync("/v1/admin/support/tickets", harness.Tokens.Admin(admin));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, allowed.StatusCode);

        // Still refused for the role the matrix refuses, so the gate is not bypassed by the outage.
        var officer = await harness.Seed.InternalUserAsync(MageRideRoles.VerificationOfficer);

        using var refused = await harness.GetAsync(
            "/v1/admin/support/tickets", harness.Tokens.Internal(officer, MageRideRoles.VerificationOfficer));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    /// <summary>
    /// A Support CSR works the queues and cannot ban anybody — URD §2.3's ◐ enforced, not ignored.
    /// </summary>
    /// <remarks>
    /// The Moderation row gives a CSR "◐ temp on reports", so they read the report queue; the
    /// platform-wide suspension is the action that qualifier does not describe, and URD §2.4 spells
    /// it out ("limited temporary actions"). The Support row gives them ✅, so tickets are theirs
    /// outright.
    /// </remarks>
    [Fact]
    public async Task A_support_agent_works_the_queues_and_cannot_suspend()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var csr = await harness.Seed.InternalUserAsync(MageRideRoles.SupportCsr);
        var bearer = harness.Tokens.Internal(csr, MageRideRoles.SupportCsr);
        var (driverId, _) = await harness.Seed.DriverWithVehicleAsync();

        using var tickets = await harness.SendAsync(
            HttpMethod.Post,
            $"/v1/admin/support/tickets/{SeedIds.Ticket:D}/resolve",
            bearer,
            new { response = "Refunded, sorry about that." });

        Assert.Equal(HttpStatusCode.OK, tickets.StatusCode);

        using var queue = await harness.GetAsync("/v1/admin/reports/queue", bearer);
        Assert.Equal(HttpStatusCode.OK, queue.StatusCode);

        using var refused = await harness.SendAsync(
            HttpMethod.Post, $"/v1/admin/drivers/{driverId:D}/suspend", bearer, new { reason = "no" });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.False(await harness.Seed.DriverIsBlockedAsync(driverId));
    }

    /// <summary>
    /// Holding a second role lifts the scope, because the union is additive (URD §2.1).
    /// </summary>
    /// <remarks>
    /// The case <c>RequiresOwnScope</c> exists to get right: a person who is both a CSR and an Admin
    /// holds Moderation · Write platform-wide from the Admin column, and a fence that looked only at
    /// "is any grant scoped" would refuse them.
    /// </remarks>
    [Fact]
    public async Task A_csr_who_is_also_an_admin_may_suspend()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var user = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);
        var (driverId, _) = await harness.Seed.DriverWithVehicleAsync();

        var bearer = harness.Tokens.Issue(
            user, MageRideApps.Admin, MageRideRoles.SupportCsr, MageRideRoles.Admin);

        using var response = await harness.SendAsync(
            HttpMethod.Post, $"/v1/admin/drivers/{driverId:D}/suspend", bearer, new { reason = "Both hats" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await harness.Seed.DriverIsBlockedAsync(driverId));
    }
}
