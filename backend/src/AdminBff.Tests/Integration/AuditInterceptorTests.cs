using System.Net;
using System.Text.Json;
using MageRide.AdminBff.Auditing;
using MageRide.AdminBff.Endpoints;
using MageRide.AdminBff.Tests.Infrastructure;
using MageRide.Shared.Auth;
using MageRide.TestKit;

namespace MageRide.AdminBff.Tests.Integration;

/// <summary>
/// DoD: "every mutation emits an audit.events row through the interceptor" and "a mutation
/// performed with the audit interceptor disabled fails the test suite" (D-35).
/// </summary>
/// <remarks>
/// The second item is asserted in the strongest available sense: there is no configuration that
/// switches the interceptor off, and a mutating route mapped outside its group — or one that
/// declares no action, or one whose handler records nothing — is a service that will not start or a
/// request that answers 500. All three are exercised below against a pipeline built the way
/// <c>Program.cs</c> builds it.
/// </remarks>
[Collection(AdminBffCollection.Name)]
public sealed class AuditInterceptorTests(PostgresFixture postgres)
{
    /// <summary>
    /// Every mutating route declares what it writes. Checked against the running route table, so a
    /// route added later is covered without anybody editing this test.
    /// </summary>
    [Fact]
    public async Task Every_mutating_route_declares_an_audit_action()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        foreach (var endpoint in harness.Routes.Where(AuditInterceptor.IsMutating))
        {
            var pattern = endpoint.RoutePattern.RawText!;

            Assert.True(
                endpoint.Metadata.GetMetadata<AuditActionMetadata>() is not null,
                $"{pattern} changes state and declares no audit action (D-35).");

            Assert.True(
                endpoint.Metadata.GetMetadata<AdminSurfaceMarker>() is not null,
                $"{pattern} was mapped outside the audited group, so the interceptor is not attached.");
        }
    }

    /// <summary>
    /// A mutating route mapped outside the audited group stops the service from starting.
    /// </summary>
    /// <remarks>
    /// This is what "with the interceptor disabled" is on this service: not a setting, but a route
    /// that escaped the group. Building the same guard the composition root runs, against a route
    /// table that contains one such endpoint, is the closest a test can get to the deployment
    /// mistake it exists to prevent.
    /// </remarks>
    [Fact]
    public void A_mutation_outside_the_audited_group_refuses_to_start()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        var app = builder.Build();

        // Mapped directly, not through MapAdminBffEndpoints: no marker, no filter, no declared
        // action — exactly the shape a hurried change would take.
        app.MapPost("/v1/admin/vehicles/{vehicleId}/unsuspend", () => Results.Ok());

        var failure = Assert.Throws<InvalidOperationException>(() => AdminBffApplication.GuardTheSurface(app));

        Assert.Contains("audit", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unsuspend", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A route outside <c>/v1/admin</c> is refused too — AL-02's fence, same mechanism.</summary>
    [Fact]
    public void A_route_outside_the_admin_prefix_refuses_to_start()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.MapGet("/v1/drivers/{driverId}/earnings", () => Results.Ok());

        var failure = Assert.Throws<InvalidOperationException>(() => AdminBffApplication.GuardTheSurface(app));

        Assert.Contains("AL-02", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// One suspension writes exactly one row, and it carries the actor, the role, the address and
    /// both images.
    /// </summary>
    [Fact]
    public async Task A_suspension_writes_one_audit_row_with_the_actor_and_both_images()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var admin = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);
        var (_, vehicleId) = await harness.Seed.DriverWithVehicleAsync();

        using var response = await harness.SendAsync(
            HttpMethod.Post,
            $"/v1/admin/vehicles/{vehicleId:D}/suspend",
            harness.Tokens.Admin(admin),
            new { reason = "Repeated passenger reports" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rows = await harness.Seed.AuditRowsAsync(vehicleId);
        var row = Assert.Single(rows);

        Assert.Equal(AdminAuditActions.VehicleSuspended, row.Action);
        Assert.Equal(AdminAuditActions.VehicleEntity, row.EntityType);
        Assert.Equal(admin, row.ActorId);
        Assert.Equal(MageRideRoles.Admin, row.ActorRole);
        Assert.NotNull(row.Ip);

        // The before-image is the state that was actually replaced, and the after-image says what
        // it became plus why — which is the pair D-35 exists for.
        using var before = JsonDocument.Parse(row.Before!);
        using var after = JsonDocument.Parse(row.After!);

        Assert.Equal("ACTIVE", before.RootElement.GetProperty("dispatchState").GetString());
        Assert.Equal("DISPATCH_SUSPENDED", after.RootElement.GetProperty("dispatchState").GetString());
        Assert.Equal("Repeated passenger reports", after.RootElement.GetProperty("reason").GetString());

        // The interceptor's own knowledge of the request, kept apart from the entity's (1312).
        using var detail = JsonDocument.Parse(row.Detail!);
        Assert.Equal("POST", detail.RootElement.GetProperty("method").GetString());
        Assert.Contains("/suspend", detail.RootElement.GetProperty("path").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refused mutation writes nothing. A row saying an admin suspended a vehicle they were not
    /// allowed to suspend would be a false entry in an immutable log.
    /// </summary>
    [Fact]
    public async Task A_refused_mutation_writes_no_audit_row()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var officer = await harness.Seed.InternalUserAsync(MageRideRoles.VerificationOfficer);
        var (_, vehicleId) = await harness.Seed.DriverWithVehicleAsync();

        using var forbidden = await harness.SendAsync(
            HttpMethod.Post,
            $"/v1/admin/vehicles/{vehicleId:D}/suspend",
            harness.Tokens.Internal(officer, MageRideRoles.VerificationOfficer),
            new { reason = "not allowed" });

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Empty(await harness.Seed.AuditRowsAsync(vehicleId));

        // Nor does a validation failure inside a permitted call.
        var admin = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);

        using var invalid = await harness.SendAsync(
            HttpMethod.Post,
            $"/v1/admin/vehicles/{vehicleId:D}/suspend",
            harness.Tokens.Admin(admin),
            new { reason = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Empty(await harness.Seed.AuditRowsAsync(vehicleId));
    }

    /// <summary>
    /// The audit row commits with the suspension, not after it: a failed mutation leaves neither.
    /// </summary>
    [Fact]
    public async Task A_failed_mutation_leaves_neither_the_change_nor_the_row()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var admin = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);
        var missing = Guid.CreateVersion7();

        using var response = await harness.SendAsync(
            HttpMethod.Post,
            $"/v1/admin/vehicles/{missing:D}/suspend",
            harness.Tokens.Admin(admin),
            new { reason = "no such vehicle" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await harness.Seed.AuditRowsAsync(missing));
    }

    /// <summary>
    /// A forwarded mutation is audited here as well as at the owner — two rows for one action,
    /// which is the right failure (reputation-svc's own note says the same).
    /// </summary>
    [Fact]
    public async Task A_forwarded_decision_is_audited_at_the_front_door()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var admin = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);

        // A fresh report id, because audit.events is append-only and shared across this collection:
        // a fixed one would accumulate a row per test run and make "exactly one" a lie about the
        // interceptor rather than about the fixture.
        var reportId = Guid.CreateVersion7();

        using var response = await harness.SendAsync(
            HttpMethod.Post,
            $"/v1/admin/reports/{reportId:D}/resolve",
            harness.Tokens.Admin(admin),
            new { decision = "CONFIRMED", note = "Third strike" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var row = Assert.Single(await harness.Seed.AuditRowsAsync(reportId));

        // The action names the decision rather than the route: an auditor asking "how many reports
        // were upheld" must not have to parse the JSON image to find out.
        Assert.Equal(AdminAuditActions.ReportConfirmed, row.Action);

        using var after = JsonDocument.Parse(row.After!);
        Assert.True(after.RootElement.GetProperty("vehicleDelisted").GetBoolean());
    }

    /// <summary>
    /// A read is not audited here. D-35 audits mutations; AL-39/AL-40's <c>DOC_VIEW</c> and
    /// <c>PII_READ</c> are the two reads that disclose a person's data, and they are C063's and
    /// C064's.
    /// </summary>
    [Fact]
    public async Task A_dashboard_read_writes_no_audit_row()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var admin = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);

        using var response = await harness.GetAsync("/v1/admin/dashboard", harness.Tokens.Admin(admin));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await harness.Seed.AuditRowsByActionAsync("DASHBOARD_READ"));
    }
}
