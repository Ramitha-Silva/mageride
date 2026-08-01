using System.Net;
using MageRide.AdminBff.Auditing;
using MageRide.AdminBff.Navigation;
using MageRide.AdminBff.Tests.Infrastructure;
using MageRide.Shared.Auth;
using MageRide.TestKit;

namespace MageRide.AdminBff.Tests.Integration;

/// <summary>
/// The post-sign-in bootstrap (URD §2.2), the AL-37 fence, and the audit log itself (US-19.3).
/// </summary>
[Collection(AdminBffCollection.Name)]
public sealed class SessionAndAuditLogTests(PostgresFixture postgres)
{
    /// <summary>
    /// DoD: "no login path requests a second factor" (AL-37).
    /// </summary>
    /// <remarks>
    /// Asserted against the running route table rather than by reading the code: there is no route
    /// on this surface that could challenge for one, because there is no auth route here at all —
    /// sign-in is iam-svc's <c>POST /v1/admin/auth/login</c>, which the gateway sends there. The
    /// session response says so explicitly as well, because D3' §0 and D7' §4.2 still carry the
    /// pre-AL-37 wording and a portal built from those would wait for a challenge.
    /// </remarks>
    [Fact]
    public async Task No_login_path_asks_for_a_second_factor()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        foreach (var endpoint in harness.Routes)
        {
            var pattern = endpoint.RoutePattern.RawText!;

            Assert.DoesNotContain("mfa", pattern, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("totp", pattern, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/auth", pattern, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("otp", pattern, StringComparison.OrdinalIgnoreCase);
        }

        var admin = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);

        using var response = await harness.GetAsync("/v1/admin/session", harness.Tokens.Admin(admin));
        using var payload = await harness.ReadJsonAsync(response);

        Assert.False(payload.RootElement.GetProperty("mfaRequired").GetBoolean());
    }

    /// <summary>
    /// The menu is a projection of URD §2.3, so two roles get two different consoles.
    /// </summary>
    [Fact]
    public async Task The_menu_manifest_is_scoped_to_the_caller()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var superAdmin = await harness.Seed.InternalUserAsync(MageRideRoles.SuperAdmin);
        var officer = await harness.Seed.InternalUserAsync(MageRideRoles.VerificationOfficer);

        using var superResponse = await harness.GetAsync("/v1/admin/session", harness.Tokens.SuperAdmin(superAdmin));
        using var superPayload = await harness.ReadJsonAsync(superResponse);

        using var officerResponse = await harness.GetAsync(
            "/v1/admin/session", harness.Tokens.Internal(officer, MageRideRoles.VerificationOfficer));

        using var officerPayload = await harness.ReadJsonAsync(officerResponse);

        var superGroups = superPayload.RootElement.GetProperty("menu")
            .EnumerateArray().Select(group => group.GetProperty("key").GetString()!).ToArray();

        var officerGroups = officerPayload.RootElement.GetProperty("menu")
            .EnumerateArray().Select(group => group.GetProperty("key").GetString()!).ToArray();

        // Only a Super Admin holds RBAC provisioning (URD §2.3's RBAC row gives even Admin ➖).
        Assert.Contains("access", superGroups);
        Assert.DoesNotContain("access", officerGroups);

        // The Verification Officer's console is the onboarding queue, and not the finance section.
        Assert.Contains("onboarding", officerGroups);
        Assert.DoesNotContain("finance", officerGroups);

        // A group with nothing in it is dropped rather than rendered as an empty heading.
        foreach (var group in officerPayload.RootElement.GetProperty("menu").EnumerateArray())
        {
            Assert.True(group.GetProperty("items").GetArrayLength() > 0);
        }
    }

    /// <summary>
    /// The Configuration group carries the four items other services answer, so the console is one
    /// console (AL-02).
    /// </summary>
    [Fact]
    public async Task The_configuration_group_names_the_items_other_services_own()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var superAdmin = await harness.Seed.InternalUserAsync(MageRideRoles.SuperAdmin);

        using var response = await harness.GetAsync("/v1/admin/session", harness.Tokens.SuperAdmin(superAdmin));
        using var payload = await harness.ReadJsonAsync(response);

        var configuration = payload.RootElement.GetProperty("menu")
            .EnumerateArray()
            .Single(group => group.GetProperty("key").GetString() == "configuration");

        var items = configuration.GetProperty("items")
            .EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("key").GetString()!,
                item => item.GetProperty("ownedBy").GetString()!,
                StringComparer.Ordinal);

        Assert.Equal("transit-svc", items["gtfs"]);
        Assert.Equal("subscription-svc", items["daily-fee-rates"]);
        Assert.Equal("subscription-svc", items["voucher-tiers"]);
        Assert.Equal("dispatch-svc", items["driver-levels"]);
        Assert.Equal("admin-bff", items["fare-tariffs"]);
    }

    /// <summary>Every nav item is gated on an area the matrix actually holds.</summary>
    [Fact]
    public void Every_menu_item_names_a_real_feature_area()
    {
        foreach (var item in AdminMenu.All.SelectMany(static group => group.Items))
        {
            Assert.NotNull(FeatureAreas.Find(item.Area.Key));

            Assert.True(
                item.Needed != PermissionGrant.None,
                $"Menu item '{item.Key}' asks for no capability, so the matrix cannot refuse it.");

            // A label, never a string: D-26 makes every user-facing word trilingual and the portal
            // owns the bundles.
            Assert.StartsWith("nav.", item.LabelKey, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The audit log reads back what the interceptor wrote, filters, and pages.
    /// </summary>
    [Fact]
    public async Task The_audit_log_reads_back_what_was_written()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var admin = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);
        var auditor = await harness.Seed.InternalUserAsync(MageRideRoles.Auditor);

        var (_, vehicleId) = await harness.Seed.DriverWithVehicleAsync();

        using (var suspended = await harness.SendAsync(
                   HttpMethod.Post,
                   $"/v1/admin/vehicles/{vehicleId:D}/suspend",
                   harness.Tokens.Admin(admin),
                   new { reason = "Audited" }))
        {
            Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);
        }

        var auditorBearer = harness.Tokens.Internal(auditor, MageRideRoles.Auditor);

        using var bySubject = await harness.GetAsync(
            $"/v1/admin/audit-log?subjectId={vehicleId:D}", auditorBearer);

        using var payload = await harness.ReadJsonAsync(bySubject);

        var row = payload.RootElement.GetProperty("items").EnumerateArray().Single();

        Assert.Equal(AdminAuditActions.VehicleSuspended, row.GetProperty("action").GetString());
        Assert.Equal(admin, row.GetProperty("actorId").GetGuid());
        Assert.Equal(MageRideRoles.Admin, row.GetProperty("actorRole").GetString());
        Assert.Equal(vehicleId, row.GetProperty("subjectId").GetGuid());

        // The stored images come back as the documents they were written as.
        Assert.Equal("Audited", row.GetProperty("after").GetProperty("reason").GetString());
        Assert.Equal("ACTIVE", row.GetProperty("before").GetProperty("dispatchState").GetString());

        // A filter that matches nothing is an empty page, not an error.
        using var byActor = await harness.GetAsync(
            $"/v1/admin/audit-log?actorId={Guid.CreateVersion7():D}", auditorBearer);

        using var empty = await harness.ReadJsonAsync(byActor);
        Assert.Empty(empty.RootElement.GetProperty("items").EnumerateArray());
    }

    /// <summary>
    /// The audit log is append-only: there is no route on this surface that could write one.
    /// </summary>
    [Fact]
    public async Task There_is_no_write_route_on_the_audit_log()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var writable = harness.Routes
            .Where(route => route.RoutePattern.RawText!.Contains("audit-log", StringComparison.Ordinal))
            .SelectMany(route => route.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
            .Where(method => method is not "GET" and not "HEAD")
            .ToArray();

        Assert.Empty(writable);
    }

    /// <summary>
    /// The audit log pages, and the cursor is stable across rows written in the same millisecond.
    /// </summary>
    /// <remarks>
    /// The reason the cursor is the identity column rather than <c>ts</c>: a suspension and its
    /// neighbours land inside one clock tick, and a timestamp cursor would drop or repeat rows at
    /// the page boundary.
    /// </remarks>
    [Fact]
    public async Task The_audit_log_pages_without_dropping_a_row()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var admin = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);
        var bearer = harness.Tokens.Admin(admin);
        var actions = new List<Guid>();

        for (var index = 0; index < 5; index++)
        {
            var (_, vehicleId) = await harness.Seed.DriverWithVehicleAsync();
            actions.Add(vehicleId);

            using var response = await harness.SendAsync(
                HttpMethod.Post, $"/v1/admin/vehicles/{vehicleId:D}/suspend", bearer, new { reason = "page" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var seen = new List<Guid>();
        string? cursor = null;

        do
        {
            // Filtered by this run's actor as well as the action: audit.events is append-only and
            // shared across the collection, so another test's suspensions are in the table too.
            var query = $"/v1/admin/audit-log?action={AdminAuditActions.VehicleSuspended}&actorId={admin:D}&limit=2"
                        + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");

            using var page = await harness.GetAsync(query, bearer);
            using var payload = await harness.ReadJsonAsync(page);

            seen.AddRange(payload.RootElement.GetProperty("items")
                .EnumerateArray()
                .Select(item => item.GetProperty("subjectId").GetGuid()));

            cursor = payload.RootElement.GetProperty("cursor").GetString();
        }
        while (cursor is not null);

        Assert.Equal(actions.Count, seen.Distinct().Count());
        Assert.All(actions, id => Assert.Contains(id, seen));
    }
}
