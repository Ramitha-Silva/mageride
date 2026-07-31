using System.Net;
using MageRide.Fleet.Endpoints;
using MageRide.Fleet.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Fleet.Tests.Integration;

/// <summary>
/// US-13.2 / US-13.8 / US-13.9 — time-bounded driver assignment, and what expiry and revocation
/// each take away.
/// </summary>
/// <remarks>
/// The C059 definition of done's third item — "an assignment expiring removes the driver's ability
/// to select that vehicle without manual action" — is
/// <see cref="An_expiring_assignment_takes_the_vehicle_away_with_nothing_written"/>.
/// </remarks>
[Collection<FleetCollection>]
public sealed class AssignmentTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_driver_is_assigned_by_id_or_by_phone_and_the_list_shows_both()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        var vehicle = await AddVehicleAsync(harness, fleet, "WP-AA-1001");
        var (byId, _) = await harness.CreateDriverAsync();
        var (byPhoneId, phone) = await harness.CreateDriverAsync();

        var first = await harness.PostJsonAsync<AssignmentResponse>(
            $"/v1/fleets/{fleet.FleetId}/assignments",
            new { driverId = byId.ToString(), vehicleId = vehicle.ToString(), from = DateTimeOffset.UtcNow },
            fleet.OwnerBearer);

        Assert.True(first.Active);
        Assert.Equal("WP-AA-1001", first.RegistrationNumber);

        // US-13.2 assigns "by User ID / phone", and an operator in a depot has the number.
        var second = await harness.PostJsonAsync<AssignmentResponse>(
            $"/v1/fleets/{fleet.FleetId}/assignments",
            new { driverPhone = phone, vehicleId = vehicle.ToString(), from = DateTimeOffset.UtcNow },
            fleet.OwnerBearer);

        Assert.Equal(byPhoneId.ToString(), second.DriverId);

        var list = await harness.GetAsync<AssignmentsResponse>(
            $"/v1/fleets/{fleet.FleetId}/assignments", fleet.OwnerBearer);

        Assert.Equal(2, list.Items.Count);
        Assert.All(list.Items, assignment => Assert.True(assignment.Active));
    }

    /// <summary>
    /// <b>Definition of done:</b> an assignment expiring removes the driver's ability to select that
    /// vehicle, without manual action.
    /// </summary>
    /// <remarks>
    /// Asserted against <c>registry.driver_eligible_vehicles</c>, which is the projection
    /// registry-svc's <c>select-live</c>, dispatch-svc's standby gate and trip-state-svc's session
    /// start all read (migrations 0310/0314) — so this is what the Driver App will actually offer,
    /// without booting three services. <b>Nothing is written between the two reads</b>: no sweep, no
    /// revocation, no job. The row is untouched and simply stops being returned, which is the whole
    /// of "without manual action".
    /// </remarks>
    [Fact]
    public async Task An_expiring_assignment_takes_the_vehicle_away_with_nothing_written()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        var vehicle = await AddVehicleAsync(harness, fleet, "WP-AB-2001");
        var (driverId, _) = await harness.CreateDriverAsync();

        // A window that has already closed — the temporary hire AL-23 exists for, two seconds after
        // the fact rather than two weeks, so the test does not sleep.
        var assignment = await harness.PostJsonAsync<AssignmentResponse>(
            $"/v1/fleets/{fleet.FleetId}/assignments",
            new
            {
                driverId = driverId.ToString(),
                vehicleId = vehicle.ToString(),
                from = DateTimeOffset.UtcNow.AddHours(-2),
                to = DateTimeOffset.UtcNow.AddHours(-1),
            },
            fleet.OwnerBearer);

        // The row exists, is not revoked, and confers nothing.
        Assert.False(assignment.Active);
        Assert.Null(assignment.RevokedAt);
        Assert.DoesNotContain(vehicle, await harness.EligibleVehiclesAsync(driverId));

        // The same driver, the same vehicle, a window that is open now: the projection returns it,
        // and the *only* difference between the two states is the passage of time.
        var live = await harness.PostJsonAsync<AssignmentResponse>(
            $"/v1/fleets/{fleet.FleetId}/assignments",
            new
            {
                driverId = driverId.ToString(),
                vehicleId = vehicle.ToString(),
                from = DateTimeOffset.UtcNow.AddMinutes(-1),
                to = DateTimeOffset.UtcNow.AddHours(8),
            },
            fleet.OwnerBearer);

        Assert.True(live.Active);
        Assert.Contains(vehicle, await harness.EligibleVehiclesAsync(driverId));

        // And a window that has not opened yet confers nothing either — a relief driver booked on
        // Monday for Thursday's shift must not be able to take the bus out on Monday.
        var (futureDriverId, _) = await harness.CreateDriverAsync();

        var booked = await harness.PostJsonAsync<AssignmentResponse>(
            $"/v1/fleets/{fleet.FleetId}/assignments",
            new
            {
                driverId = futureDriverId.ToString(),
                vehicleId = vehicle.ToString(),
                from = DateTimeOffset.UtcNow.AddDays(3),
                to = DateTimeOffset.UtcNow.AddDays(4),
            },
            fleet.OwnerBearer);

        Assert.False(booked.Active);
        Assert.Empty(await harness.EligibleVehiclesAsync(futureDriverId));
    }

    /// <summary>US-13.8: revoking takes the vehicle away at once, and history survives.</summary>
    [Fact]
    public async Task Revoking_an_assignment_takes_the_vehicle_away_and_leaves_the_row()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        var vehicle = await AddVehicleAsync(harness, fleet, "WP-AC-3001");
        var (driverId, _) = await harness.CreateDriverAsync();

        var assignment = await harness.PostJsonAsync<AssignmentResponse>(
            $"/v1/fleets/{fleet.FleetId}/assignments",
            new { driverId = driverId.ToString(), vehicleId = vehicle.ToString(), from = DateTimeOffset.UtcNow },
            fleet.OwnerBearer);

        Assert.Contains(vehicle, await harness.EligibleVehiclesAsync(driverId));

        using var revoked = await harness.DeleteAsync(
            $"/v1/fleets/{fleet.FleetId}/assignments/{assignment.AssignmentId}", fleet.OwnerBearer);

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.DoesNotContain(vehicle, await harness.EligibleVehiclesAsync(driverId));

        // SCR-FP-005 shows assignment history, so the row stays and reads inactive.
        var list = await harness.GetAsync<AssignmentsResponse>(
            $"/v1/fleets/{fleet.FleetId}/assignments", fleet.OwnerBearer);

        var ended = Assert.Single(list.Items);

        Assert.False(ended.Active);
        Assert.NotNull(ended.RevokedAt);

        // Revoking twice is a 404: the second is a client acting on a stale list.
        using var again = await harness.DeleteAsync(
            $"/v1/fleets/{fleet.FleetId}/assignments/{assignment.AssignmentId}", fleet.OwnerBearer);

        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    /// <summary>
    /// <c>ex_fleet_assign_overlap</c>: one open assignment per (driver, vehicle) at any instant, and
    /// consecutive windows are how a relief driver is re-hired.
    /// </summary>
    [Fact]
    public async Task Overlapping_windows_conflict_and_consecutive_ones_do_not()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        var vehicle = await AddVehicleAsync(harness, fleet, "WP-AD-4001");
        var (driverId, _) = await harness.CreateDriverAsync();

        var start = DateTimeOffset.UtcNow.AddDays(1);

        await harness.PostJsonAsync<AssignmentResponse>(
            $"/v1/fleets/{fleet.FleetId}/assignments",
            new
            {
                driverId = driverId.ToString(),
                vehicleId = vehicle.ToString(),
                from = start,
                to = start.AddDays(7),
            },
            fleet.OwnerBearer);

        using var overlapping = await harness.PostAsync(
            $"/v1/fleets/{fleet.FleetId}/assignments",
            new
            {
                driverId = driverId.ToString(),
                vehicleId = vehicle.ToString(),
                from = start.AddDays(3),
                to = start.AddDays(10),
            },
            fleet.OwnerBearer);

        var problem = await FleetHarness.ProblemAsync(overlapping);

        Assert.Equal(HttpStatusCode.Conflict, problem.Status);

        // Next month, same driver, same bus. 0306's unique index would have refused this for ever.
        var next = await harness.PostJsonAsync<AssignmentResponse>(
            $"/v1/fleets/{fleet.FleetId}/assignments",
            new
            {
                driverId = driverId.ToString(),
                vehicleId = vehicle.ToString(),
                from = start.AddDays(30),
                to = start.AddDays(37),
            },
            fleet.OwnerBearer);

        Assert.False(next.Active);
    }

    [Fact]
    public async Task Assigning_somebody_who_is_not_a_driver_is_refused_and_so_is_a_vehicle_that_is_not_ours()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var mine = await harness.CreateFleetAsync();
        var theirs = await harness.CreateFleetAsync();

        await harness.ApproveAsync(mine.FleetId);
        await harness.ApproveAsync(theirs.FleetId);

        var vehicle = await AddVehicleAsync(harness, mine, "WP-AE-5001");
        var theirVehicle = await AddVehicleAsync(harness, theirs, "WP-AF-5002");
        var passengerId = await harness.CreateUserAsync("passenger");

        using var notADriver = await harness.PostAsync(
            $"/v1/fleets/{mine.FleetId}/assignments",
            new { driverId = passengerId.ToString(), vehicleId = vehicle.ToString(), from = DateTimeOffset.UtcNow },
            mine.OwnerBearer);

        var refused = await FleetHarness.ProblemAsync(notADriver);

        Assert.Equal(HttpStatusCode.NotFound, refused.Status);
        Assert.Equal("driver-not-found", refused.Code);

        var (driverId, _) = await harness.CreateDriverAsync();

        using var crossOrg = await harness.PostAsync(
            $"/v1/fleets/{mine.FleetId}/assignments",
            new { driverId = driverId.ToString(), vehicleId = theirVehicle.ToString(), from = DateTimeOffset.UtcNow },
            mine.OwnerBearer);

        var wrongFleet = await FleetHarness.ProblemAsync(crossOrg);

        // A different code from the driver's, so a portal can tell which half of the request was
        // wrong without guessing.
        Assert.Equal(HttpStatusCode.NotFound, wrongFleet.Status);
        Assert.Equal("vehicle-not-found", wrongFleet.Code);
    }

    [Fact]
    public async Task An_assignment_needs_a_start_and_a_window_that_makes_sense()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        var vehicle = await AddVehicleAsync(harness, fleet, "WP-AG-6001");
        var (driverId, _) = await harness.CreateDriverAsync();

        using var noStart = await harness.PostAsync(
            $"/v1/fleets/{fleet.FleetId}/assignments",
            new { driverId = driverId.ToString(), vehicleId = vehicle.ToString() },
            fleet.OwnerBearer);

        var missing = await FleetHarness.ProblemAsync(noStart);

        Assert.Equal(HttpStatusCode.BadRequest, missing.Status);
        Assert.Contains("from", missing.Body, StringComparison.Ordinal);

        using var backwards = await harness.PostAsync(
            $"/v1/fleets/{fleet.FleetId}/assignments",
            new
            {
                driverId = driverId.ToString(),
                vehicleId = vehicle.ToString(),
                from = DateTimeOffset.UtcNow.AddDays(2),
                to = DateTimeOffset.UtcNow.AddDays(1),
            },
            fleet.OwnerBearer);

        var inverted = await FleetHarness.ProblemAsync(backwards);

        Assert.Equal(HttpStatusCode.BadRequest, inverted.Status);
    }

    private static async Task<Guid> AddVehicleAsync(
        FleetHarness harness, SeededFleet fleet, string registration)
    {
        var vehicle = await harness.PostJsonAsync<FleetVehicleResponse>(
            $"/v1/fleets/{fleet.FleetId}/vehicles",
            new { registrationNumber = registration, vehicleType = "van", mode = "B" },
            fleet.OwnerBearer);

        return Guid.Parse(vehicle.VehicleId);
    }
}
