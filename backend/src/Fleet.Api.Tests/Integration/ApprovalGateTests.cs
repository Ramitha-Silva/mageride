using System.Net;
using MageRide.Fleet.Authorization;
using MageRide.Fleet.Endpoints;
using MageRide.Fleet.Tests.Infrastructure;
using MageRide.TestKit;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Fleet.Tests.Integration;

/// <summary>
/// US-13.A7 — the definition-of-done claim that "an unapproved org receives 403 on every vehicle
/// and assignment endpoint", and the AL-49 classification gate the payout profile exists for.
/// </summary>
[Collection<FleetCollection>]
public sealed class ApprovalGateTests(PostgresFixture postgres)
{
    [Fact]
    public async Task An_unapproved_organisation_is_refused_on_a_vehicle_route_and_told_why()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();
        var vehicleId = await harness.AddVehicleAsync(fleet.FleetId, fleet.OwnerId);

        using var response = await harness.PutAsync(
            $"/v1/fleets/{fleet.FleetId}/vehicles/{vehicleId}/classification",
            new { modeBBilling = "free" },
            fleet.OwnerBearer);

        var problem = await FleetHarness.ProblemAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, problem.Status);
        // A code of its own rather than a bare `forbidden`: SCR-FP-002 renders "we are reviewing
        // your application", which is not "you may not do this".
        Assert.Equal("fleet-not-approved", problem.Code);

        // Reading the organisation is not gated — a PENDING org's owner has to be able to see
        // that it is pending.
        var read = await harness.GetAsync<FleetResponse>($"/v1/fleets/{fleet.FleetId}", fleet.OwnerBearer);
        Assert.Equal("PENDING", read.Status);

        // And once an officer approves, the same request succeeds.
        await harness.ApproveAsync(fleet.FleetId);

        var classified = await harness.PutAsync<FleetVehicleResponse>(
            $"/v1/fleets/{fleet.FleetId}/vehicles/{vehicleId}/classification",
            new { modeBBilling = "free" },
            fleet.OwnerBearer);

        Assert.Equal("free", classified.ModeBBilling);
    }

    [Fact]
    public async Task A_rejected_organisation_is_refused_on_the_same_routes()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();
        var vehicleId = await harness.AddVehicleAsync(fleet.FleetId, fleet.OwnerId);
        var officerId = await harness.CreateUserAsync("verification_officer");

        using var rejection = await harness.InternalAsync(
            HttpMethod.Post,
            $"/v1/internal/fleets/{fleet.FleetId}/reject",
            new { officerId = officerId.ToString(), reason = "Business registration could not be confirmed." });

        Assert.True(rejection.IsSuccessStatusCode);

        using var response = await harness.PutAsync(
            $"/v1/fleets/{fleet.FleetId}/vehicles/{vehicleId}/classification",
            new { modeBBilling = "free" },
            fleet.OwnerBearer);

        var problem = await FleetHarness.ProblemAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, problem.Status);
        Assert.Equal("fleet-not-approved", problem.Code);
    }

    /// <summary>
    /// The gate is on the two groups, so it cannot be forgotten by a route added later.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The definition of done says "<b>every</b> vehicle and assignment endpoint", and this
    /// component maps one of them. What keeps the claim true as C059 adds onboarding, bulk CSV,
    /// documents, assignment and tracker binding is that all of them hang off
    /// <see cref="FleetEndpoints.FleetVehiclesGroup"/> and
    /// <see cref="FleetEndpoints.FleetAssignmentsGroup"/> — and this test walks the endpoint data
    /// source and fails if any route under either prefix is missing either filter.
    /// </para>
    /// <para>
    /// It therefore fails for the <em>next</em> component's mistake, which is the point.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Every_vehicle_and_assignment_route_is_gated()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var routes = harness.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint =>
                endpoint.RoutePattern.RawText is { } pattern
                && (pattern.StartsWith(FleetEndpoints.FleetVehiclesGroup, StringComparison.Ordinal)
                    || pattern.StartsWith(FleetEndpoints.FleetAssignmentsGroup, StringComparison.Ordinal)))
            .ToArray();

        // If this ever reaches zero the assertions below pass vacuously, which would be worse than
        // a failure — a green suite claiming a guarantee nothing is under.
        Assert.NotEmpty(routes);

        foreach (var endpoint in routes)
        {
            Assert.True(
                endpoint.Metadata.GetMetadata<RequiresApprovedFleet>() is not null,
                $"{endpoint.RoutePattern.RawText} is not behind the US-13.A7 approval gate. "
                + "Map it on the group FleetEndpoints builds, not on a group of its own.");

            Assert.True(
                endpoint.Metadata.GetMetadata<RequiredFleetRole>() is not null,
                $"{endpoint.RoutePattern.RawText} declares no minimum fleet sub-role (AL-03). "
                + "Add .RequireFleetSubRole(...) — without it a Viewer can call it.");
        }
    }

    /// <summary>
    /// BR-31.1: Paid needs a verified payout profile, and the refusal is the kernel's 409.
    /// </summary>
    [Fact]
    public async Task Paid_classification_is_refused_until_the_payout_profile_is_verified()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        var vehicleId = await harness.AddVehicleAsync(fleet.FleetId, fleet.OwnerId);

        // No profile at all.
        using var noProfile = await harness.PutAsync(
            $"/v1/fleets/{fleet.FleetId}/vehicles/{vehicleId}/classification",
            new { modeBBilling = "paid", defaultMonthlyFareMinor = 250_000 },
            fleet.OwnerBearer);

        var first = await FleetHarness.ProblemAsync(noProfile);
        Assert.Equal(HttpStatusCode.Conflict, first.Status);
        Assert.Equal("payout-profile-not-verified", first.Code);

        // Free is never gated — an office shuttle collects nothing.
        var free = await harness.PutAsync<FleetVehicleResponse>(
            $"/v1/fleets/{fleet.FleetId}/vehicles/{vehicleId}/classification",
            new { modeBBilling = "free" },
            fleet.OwnerBearer);

        Assert.Equal("free", free.ModeBBilling);
        Assert.Null(free.DefaultMonthlyFareMinor);

        // Submitted but not yet verified — still refused. This is the state an operator is in for
        // however long the officer queue takes, and it is the one that must not leak.
        await harness.PutAsync<PayoutProfileResponse>(
            $"/v1/fleets/{fleet.FleetId}/payout-profile",
            new
            {
                bank = "Bank of Ceylon",
                branch = "Nugegoda",
                accountNo = "0071234567",
                accountHolderName = "Ruhunu Express (Pvt) Ltd",
            },
            fleet.OwnerBearer);

        using var pending = await harness.PutAsync(
            $"/v1/fleets/{fleet.FleetId}/vehicles/{vehicleId}/classification",
            new { modeBBilling = "paid", defaultMonthlyFareMinor = 250_000 },
            fleet.OwnerBearer);

        var second = await FleetHarness.ProblemAsync(pending);
        Assert.Equal(HttpStatusCode.Conflict, second.Status);
        Assert.Equal("payout-profile-not-verified", second.Code);

        // Nothing was written on the way to either refusal.
        Assert.Equal("free", await harness.VehicleBillingAsync(vehicleId));

        await harness.ApproveAsync(fleet.FleetId);

        var paid = await harness.PutAsync<FleetVehicleResponse>(
            $"/v1/fleets/{fleet.FleetId}/vehicles/{vehicleId}/classification",
            new { modeBBilling = "paid", defaultMonthlyFareMinor = 250_000 },
            fleet.OwnerBearer);

        Assert.Equal("paid", paid.ModeBBilling);
        Assert.Equal(250_000, paid.DefaultMonthlyFareMinor);
        Assert.Equal("LKR", paid.Currency);
    }

    [Fact]
    public async Task Paid_without_a_fare_is_refused_and_a_mode_A_vehicle_has_no_service_payment()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        var modeB = await harness.AddVehicleAsync(fleet.FleetId, fleet.OwnerId, mode: "B");
        var modeA = await harness.AddVehicleAsync(fleet.FleetId, fleet.OwnerId, mode: "A", type: "bus");

        using var noFare = await harness.PutAsync(
            $"/v1/fleets/{fleet.FleetId}/vehicles/{modeB}/classification",
            new { modeBBilling = "paid" },
            fleet.OwnerBearer);

        var missingFare = await FleetHarness.ProblemAsync(noFare);
        Assert.Equal(HttpStatusCode.BadRequest, missingFare.Status);
        Assert.Contains("defaultMonthlyFareMinor", missingFare.Body, StringComparison.Ordinal);

        // AL-24: mode_b_billing is NULL for Mode A and C by design. A bus has no subscribers.
        using var bus = await harness.PutAsync(
            $"/v1/fleets/{fleet.FleetId}/vehicles/{modeA}/classification",
            new { modeBBilling = "free" },
            fleet.OwnerBearer);

        var wrongMode = await FleetHarness.ProblemAsync(bus);
        Assert.Equal(HttpStatusCode.BadRequest, wrongMode.Status);
        Assert.Contains("Mode B vehicles only", wrongMode.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_vehicle_that_is_not_in_the_fleet_is_not_found()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var mine = await harness.CreateFleetAsync();
        var theirs = await harness.CreateFleetAsync();

        await harness.ApproveAsync(mine.FleetId);

        // A real vehicle, in somebody else's fleet.
        var vehicleId = await harness.AddVehicleAsync(theirs.FleetId, theirs.OwnerId);

        using var response = await harness.PutAsync(
            $"/v1/fleets/{mine.FleetId}/vehicles/{vehicleId}/classification",
            new { modeBBilling = "free" },
            mine.OwnerBearer);

        var problem = await FleetHarness.ProblemAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, problem.Status);
        Assert.Equal("vehicle-not-found", problem.Code);
    }
}
