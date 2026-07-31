using System.Net;
using MageRide.Fleet.Endpoints;
using MageRide.Fleet.Tests.Infrastructure;
using MageRide.Shared.Auth;
using MageRide.TestKit;

namespace MageRide.Fleet.Tests.Integration;

/// <summary>
/// US-13.1 / US-13.7 — adding Mode A and Mode B vehicles to an organisation's roster, and taking
/// them off it.
/// </summary>
[Collection<FleetCollection>]
public sealed class VehicleOnboardingTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_mode_A_and_a_mode_B_vehicle_are_onboarded_and_start_pending_and_docs_pending()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        var bus = await harness.PostJsonAsync<FleetVehicleResponse>(
            $"/v1/fleets/{fleet.FleetId}/vehicles",
            new { registrationNumber = "wp na-4471", vehicleType = "bus", mode = "A" },
            fleet.OwnerBearer);

        Assert.Equal("A", bus.Mode);

        // Canonicalised, not merely accepted. D-37's uniqueness is a unique index over the stored
        // text, so a Fleet Portal that stored "wp na-4471" beside the Driver App's "WP-NA-4471"
        // would let one plate exist twice.
        Assert.Equal("WP-NA-4471", bus.RegistrationNumber);

        // No auto-approval on this surface: AL-30's is the Mode C wizard's, and AL-50 puts a route
        // permit in front of a person.
        Assert.Equal("PENDING", bus.Status);
        Assert.Equal("docs_pending", bus.DocsStatus);

        var van = await harness.PostJsonAsync<FleetVehicleResponse>(
            $"/v1/fleets/{fleet.FleetId}/vehicles",
            new { registrationNumber = "WP-QQ-1002", vehicleType = "van", mode = "B" },
            fleet.OwnerBearer);

        Assert.Equal("B", van.Mode);

        // Unclassified, not defaulted to free. AL-24 makes NULL "nobody has named a price", which
        // is what subscription-svc reads it as.
        Assert.Null(van.ModeBBilling);

        var roster = await harness.GetAsync<FleetVehiclesResponse>(
            $"/v1/fleets/{fleet.FleetId}/vehicles", fleet.OwnerBearer);

        Assert.Equal(2, roster.Items.Count);
        Assert.All(roster.Items, vehicle => Assert.Equal("docs_pending", vehicle.DocsStatus));
    }

    /// <summary>AL-03's fence, from the outside: Mode C is never a fleet option.</summary>
    [Fact]
    public async Task Mode_C_is_refused_and_so_is_a_train()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        using var modeC = await harness.PostAsync(
            $"/v1/fleets/{fleet.FleetId}/vehicles",
            new { registrationNumber = "WP-CC-0001", vehicleType = "three_wheeler", mode = "C" },
            fleet.OwnerBearer);

        var refused = await FleetHarness.ProblemAsync(modeC);

        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Equal("mode-not-allowed", refused.Code);

        // A real type on the wrong surface. US-2.17/2.18 make trains admin-only and give them
        // POST /v1/admin/trains.
        using var train = await harness.PostAsync(
            $"/v1/fleets/{fleet.FleetId}/vehicles",
            new { registrationNumber = "SLR-M2-0007", vehicleType = "train", mode = "A" },
            fleet.OwnerBearer);

        var admin = await FleetHarness.ProblemAsync(train);

        Assert.Equal(HttpStatusCode.Forbidden, admin.Status);
        Assert.Equal("mode-not-allowed", admin.Code);

        // And a type that does not exist at all is a 400 rather than a 403 — the value is wrong,
        // not the surface. AL-09: there is no `car`.
        using var car = await harness.PostAsync(
            $"/v1/fleets/{fleet.FleetId}/vehicles",
            new { registrationNumber = "WP-DD-0002", vehicleType = "car", mode = "B" },
            fleet.OwnerBearer);

        var unknown = await FleetHarness.ProblemAsync(car);

        Assert.Equal(HttpStatusCode.BadRequest, unknown.Status);
        Assert.Equal("invalid-vehicle-type", unknown.Code);
    }

    /// <summary>D-37: a live plate belongs to one registration, whichever surface typed it.</summary>
    [Fact]
    public async Task A_plate_that_is_already_live_is_a_conflict_however_it_is_spelled()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        await harness.PostJsonAsync<FleetVehicleResponse>(
            $"/v1/fleets/{fleet.FleetId}/vehicles",
            new { registrationNumber = "WP-NB-5510", vehicleType = "van", mode = "B" },
            fleet.OwnerBearer);

        using var again = await harness.PostAsync(
            $"/v1/fleets/{fleet.FleetId}/vehicles",
            new { registrationNumber = "wp nb 5510", vehicleType = "van", mode = "B" },
            fleet.OwnerBearer);

        var problem = await FleetHarness.ProblemAsync(again);

        Assert.Equal(HttpStatusCode.Conflict, problem.Status);
        Assert.Equal("registration-exists", problem.Code);
    }

    /// <summary>
    /// US-13.7: removing a vehicle ends its drivers' hold on it, in the same transaction.
    /// </summary>
    [Fact]
    public async Task Removing_a_vehicle_deactivates_it_and_ends_every_open_assignment()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        var vehicle = await harness.PostJsonAsync<FleetVehicleResponse>(
            $"/v1/fleets/{fleet.FleetId}/vehicles",
            new { registrationNumber = "WP-NC-6620", vehicleType = "van", mode = "B" },
            fleet.OwnerBearer);

        var vehicleId = Guid.Parse(vehicle.VehicleId);
        var (driverId, _) = await harness.CreateDriverAsync();

        await harness.PostJsonAsync<AssignmentResponse>(
            $"/v1/fleets/{fleet.FleetId}/assignments",
            new
            {
                driverId = driverId.ToString(),
                vehicleId = vehicle.VehicleId,
                from = DateTimeOffset.UtcNow.AddMinutes(-5),
            },
            fleet.OwnerBearer);

        Assert.Contains(vehicleId, await harness.EligibleVehiclesAsync(driverId));

        using var removed = await harness.DeleteAsync(
            $"/v1/fleets/{fleet.FleetId}/vehicles/{vehicleId}", fleet.OwnerBearer);

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        // Off the roster, off the road, and out of the driver's hands — all three, or the vehicle
        // is "removed" and still drivable.
        Assert.Equal("DEACTIVATED", await harness.VehicleStatusAsync(vehicleId));
        Assert.DoesNotContain(vehicleId, await harness.EligibleVehiclesAsync(driverId));

        var roster = await harness.GetAsync<FleetVehiclesResponse>(
            $"/v1/fleets/{fleet.FleetId}/vehicles", fleet.OwnerBearer);

        Assert.Empty(roster.Items);

        // DEACTIVATED is outside ux_vehicles_regno_active's predicate, so the plate is free again.
        var reused = await harness.PostJsonAsync<FleetVehicleResponse>(
            $"/v1/fleets/{fleet.FleetId}/vehicles",
            new { registrationNumber = "WP-NC-6620", vehicleType = "van", mode = "B" },
            fleet.OwnerBearer);

        Assert.NotEqual(vehicle.VehicleId, reused.VehicleId);
    }

    /// <summary>
    /// The Service payment pair may ride the onboarding request (US-13.1b), and BR-31.1 still holds.
    /// </summary>
    [Fact]
    public async Task Onboarding_can_carry_the_service_payment_and_paid_still_needs_a_verified_profile()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        var free = await harness.PostJsonAsync<FleetVehicleResponse>(
            $"/v1/fleets/{fleet.FleetId}/vehicles",
            new { registrationNumber = "WP-ND-7730", vehicleType = "van", mode = "B", modeBBilling = "free" },
            fleet.OwnerBearer);

        Assert.Equal("free", free.ModeBBilling);

        using var paid = await harness.PostAsync(
            $"/v1/fleets/{fleet.FleetId}/vehicles",
            new
            {
                registrationNumber = "WP-NE-8840",
                vehicleType = "van",
                mode = "B",
                modeBBilling = "paid",
                defaultMonthlyFareMinor = 250_000,
            },
            fleet.OwnerBearer);

        var problem = await FleetHarness.ProblemAsync(paid);

        Assert.Equal(HttpStatusCode.Conflict, problem.Status);
        Assert.Equal("payout-profile-not-verified", problem.Code);

        // The vehicle exists all the same — the classification is a second transaction, and the
        // 409 is about the price, not about the bus. SCR-FP-004 renders it as "Service payment: not
        // set" and the operator fixes it with the toggle.
        var roster = await harness.GetAsync<FleetVehiclesResponse>(
            $"/v1/fleets/{fleet.FleetId}/vehicles", fleet.OwnerBearer);

        var unclassified = Assert.Single(roster.Items, vehicle => vehicle.RegistrationNumber == "WP-NE-8840");
        Assert.Null(unclassified.ModeBBilling);
    }

    /// <summary>US-13.A5: a Viewer monitors, and changes nothing.</summary>
    [Fact]
    public async Task A_viewer_can_read_the_roster_and_cannot_add_to_it()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        var viewer = await harness.PostJsonAsync<FleetMemberResponse>(
            $"/v1/fleets/{fleet.FleetId}/members",
            new { email = $"viewer-{Guid.NewGuid():N}@example.lk", fleetRole = FleetRoles.Viewer },
            fleet.OwnerBearer);

        var bearer = harness.Tokens.FleetMember(Guid.Parse(viewer.MemberId), fleet.FleetId, FleetRoles.Viewer);

        var roster = await harness.GetAsync<FleetVehiclesResponse>(
            $"/v1/fleets/{fleet.FleetId}/vehicles", bearer);

        Assert.Empty(roster.Items);

        using var refused = await harness.PostAsync(
            $"/v1/fleets/{fleet.FleetId}/vehicles",
            new { registrationNumber = "WP-NF-9950", vehicleType = "van", mode = "B" },
            bearer);

        var problem = await FleetHarness.ProblemAsync(refused);

        Assert.Equal(HttpStatusCode.Forbidden, problem.Status);
        Assert.Equal("fleet-role-insufficient", problem.Code);
    }
}
