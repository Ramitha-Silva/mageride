using System.Net;
using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Persistence;
using MageRide.Safety.Domain;
using MageRide.Safety.Endpoints;
using MageRide.Safety.Tests.Infrastructure;
using MageRide.Shared.Primitives;
using MageRide.TestKit;

namespace MageRide.Safety.Tests.Integration;

/// <summary>US-12.5, US-12.6 and US-12.10 — reports, moderation, and the block that reaches dispatch.</summary>
[Collection(SafetyCollection.Name)]
public sealed class ReportBlockTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>
    /// The fourth definition of done, and it is asserted against <b>dispatch-svc's own candidate
    /// query</b> (C023's <c>CandidateRepository</c>) rather than a copy of the rule.
    /// </summary>
    [Fact]
    public async Task A_blocked_driver_is_never_offered_that_passengers_ride()
    {
        await using var harness = await SafetyHarness.StartAsync(postgres, redis);

        var passenger = await harness.Seed.UserAsync();
        var blocked = await harness.Seed.UserAsync(role: "driver");
        var available = await harness.Seed.UserAsync(role: "driver");

        var blockedVehicle = await harness.Seed.VehicleAsync(blocked.Id);
        var availableVehicle = await harness.Seed.VehicleAsync(available.Id);

        // Both standing by, metres apart, so nothing but the block can tell them apart.
        await harness.Seed.PresenceAsync(blocked.Id, blockedVehicle, 6.9271, 79.8612);
        await harness.Seed.PresenceAsync(available.Id, availableVehicle, 6.9272, 79.8613);

        var rideId = await harness.Seed.RideAsync(passenger.Id, state: "Matching");

        // Before the block: dispatch would offer the ride to both.
        Assert.Equal(2, (await NarrowAsync(harness, rideId, passenger.Id, [blocked.Id, available.Id])).Count);

        using (var response = await harness.PostAsync(
                   $"/v1/drivers/{blocked.Id}/block",
                   new { reason = "drove dangerously" },
                   harness.Tokens.Passenger(passenger.Id)))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        var candidates = await NarrowAsync(harness, rideId, passenger.Id, [blocked.Id, available.Id]);

        Assert.Equal([available.Id], candidates.Select(static candidate => candidate.DriverId));

        // One-directional (0903): another passenger's ride still reaches the same driver.
        var somebodyElse = await harness.Seed.UserAsync();
        var otherRide = await harness.Seed.RideAsync(somebodyElse.Id, state: "Matching");

        Assert.Equal(
            2, (await NarrowAsync(harness, otherRide, somebodyElse.Id, [blocked.Id, available.Id])).Count);
    }

    [Fact]
    public async Task Unblocking_makes_the_driver_a_candidate_again()
    {
        await using var harness = await SafetyHarness.StartAsync(postgres, redis);

        var passenger = await harness.Seed.UserAsync();
        var driver = await harness.Seed.UserAsync(role: "driver");
        var vehicleId = await harness.Seed.VehicleAsync(driver.Id);

        await harness.Seed.PresenceAsync(driver.Id, vehicleId, 6.9271, 79.8612);

        var rideId = await harness.Seed.RideAsync(passenger.Id, state: "Matching");
        var bearer = harness.Tokens.Passenger(passenger.Id);

        using (var blocked = await harness.PostAsync($"/v1/drivers/{driver.Id}/block", null, bearer))
        {
            Assert.Equal(HttpStatusCode.NoContent, blocked.StatusCode);
        }

        Assert.Empty(await NarrowAsync(harness, rideId, passenger.Id, [driver.Id]));

        using (var unblocked = await harness.DeleteAsync($"/v1/drivers/{driver.Id}/block", bearer))
        {
            Assert.Equal(HttpStatusCode.NoContent, unblocked.StatusCode);
        }

        Assert.Single(await NarrowAsync(harness, rideId, passenger.Id, [driver.Id]));
    }

    /// <summary>Blocking twice is a client that tapped twice, not an error.</summary>
    [Fact]
    public async Task Blocking_twice_is_idempotent_and_unblocking_nothing_is_a_404()
    {
        await using var harness = await SafetyHarness.StartAsync(postgres, redis);

        var passenger = await harness.Seed.UserAsync();
        var driver = await harness.Seed.UserAsync(role: "driver");
        var bearer = harness.Tokens.Passenger(passenger.Id);

        for (var i = 0; i < 2; i++)
        {
            using var blocked = await harness.PostAsync($"/v1/drivers/{driver.Id}/block", null, bearer);
            Assert.Equal(HttpStatusCode.NoContent, blocked.StatusCode);
        }

        using (var cleared = await harness.DeleteAsync($"/v1/drivers/{driver.Id}/block", bearer))
        {
            Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        }

        // 404, not 204: a client that thinks it cleared a block that was never there would show the
        // driver as available when nothing changed.
        using var again = await harness.DeleteAsync($"/v1/drivers/{driver.Id}/block", bearer);

        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task A_passenger_cannot_block_themselves()
    {
        await using var harness = await SafetyHarness.StartAsync(postgres, redis);

        var passenger = await harness.Seed.UserAsync();

        using var response = await harness.PostAsync(
            $"/v1/drivers/{passenger.Id}/block", null, harness.Tokens.Passenger(passenger.Id));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// US-12.5: the report names the driver it counts against, resolved from the ride at report
    /// time — a vehicle has an owner, not a driver.
    /// </summary>
    [Fact]
    public async Task A_report_names_the_driver_on_the_reported_ride()
    {
        await using var harness = await SafetyHarness.StartAsync(postgres, redis);

        var passenger = await harness.Seed.UserAsync();
        var driver = await harness.Seed.UserAsync(role: "driver");
        var vehicleId = await harness.Seed.VehicleAsync(driver.Id);
        var rideId = await harness.Seed.RideAsync(passenger.Id, driver.Id, vehicleId);

        using var response = await harness.PostAsync(
            "/v1/reports/vehicle",
            new { vehicleId, reason = "drove through a red light", tripId = rideId },
            harness.Tokens.Passenger(passenger.Id));

        var report = await SafetyHarness.OkAsync<VehicleReportResponse>(response, "POST /v1/reports/vehicle");

        Assert.Equal(VehicleReportStatuses.Pending, report.Status);

        var stored = Assert.Single(await harness.ReportsAsync());

        Assert.Equal(driver.Id, stored.DriverId);
        Assert.Equal(vehicleId, stored.VehicleId);
        Assert.Equal(rideId, stored.RideId);
    }

    [Fact]
    public async Task A_report_against_a_vehicle_that_does_not_exist_is_refused()
    {
        await using var harness = await SafetyHarness.StartAsync(postgres, redis);

        var passenger = await harness.Seed.UserAsync();

        using var response = await harness.PostAsync(
            "/v1/reports/vehicle",
            new { vehicleId = Guid.NewGuid(), reason = "no such car" },
            harness.Tokens.Passenger(passenger.Id));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var (code, _) = await SafetyHarness.ProblemAsync(response);
        Assert.Equal("vehicle-not-found", code);
    }

    /// <summary>
    /// US-12.6: the third confirmation is what delists — a consequence of the decision rather than a
    /// separate admin action.
    /// </summary>
    [Fact]
    public async Task The_third_confirmed_report_is_the_one_that_delists()
    {
        await using var harness = await SafetyHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.UserAsync(role: "driver");
        var vehicleId = await harness.Seed.VehicleAsync(driver.Id);

        var reports = new List<Guid>();

        for (var i = 0; i < 3; i++)
        {
            var reporter = await harness.Seed.UserAsync();

            // Settled, which is both what a passenger reports afterwards and what
            // `ux_rides_driver_busy` (C004) allows three of for one driver.
            var rideId = await harness.Seed.RideAsync(reporter.Id, driver.Id, vehicleId, state: "Paid");

            var filed = await SafetyHarness.OkAsync<VehicleReportResponse>(
                await harness.PostAsync(
                    "/v1/reports/vehicle",
                    new { vehicleId, reason = $"complaint {i}", tripId = rideId },
                    harness.Tokens.Passenger(reporter.Id)),
                "file report");

            reports.Add(filed.ReportId);
        }

        var moderator = await harness.Seed.UserAsync(role: "admin");

        for (var i = 0; i < 3; i++)
        {
            using var resolved = await harness.InternalAsync(
                HttpMethod.Post,
                $"/v1/internal/safety/reports/{reports[i]}/resolve",
                new { decision = "CONFIRMED", resolvedBy = moderator.Id, note = "reviewed" });

            var outcome = await SafetyHarness.OkAsync<ResolveReportResponse>(resolved, "resolve");

            Assert.Equal(VehicleReportStatuses.Confirmed, outcome.Status);
            Assert.Equal(i + 1, outcome.ConfirmedTotal);

            // Only the third one delists.
            Assert.Equal(i == 2, outcome.Delisted);
        }

        // The evidence behind a delisting somebody will appeal: who decided, and when.
        Assert.All(await harness.ReportsAsync(), report =>
        {
            Assert.Equal(VehicleReportStatuses.Confirmed, report.Status);
            Assert.Equal(moderator.Id, report.ResolvedBy);
            Assert.NotNull(report.ResolvedAt);
        });
    }

    /// <summary>Two moderators on one report resolve one decision between them.</summary>
    [Fact]
    public async Task A_report_cannot_be_resolved_twice()
    {
        await using var harness = await SafetyHarness.StartAsync(postgres, redis);

        var reporter = await harness.Seed.UserAsync();
        var driver = await harness.Seed.UserAsync(role: "driver");
        var vehicleId = await harness.Seed.VehicleAsync(driver.Id);
        var rideId = await harness.Seed.RideAsync(reporter.Id, driver.Id, vehicleId);

        var filed = await SafetyHarness.OkAsync<VehicleReportResponse>(
            await harness.PostAsync(
                "/v1/reports/vehicle",
                new { vehicleId, reason = "complaint", tripId = rideId },
                harness.Tokens.Passenger(reporter.Id)),
            "file report");

        using (var first = await harness.InternalAsync(
                   HttpMethod.Post,
                   $"/v1/internal/safety/reports/{filed.ReportId}/resolve",
                   new { decision = "DISMISSED" }))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using var second = await harness.InternalAsync(
            HttpMethod.Post,
            $"/v1/internal/safety/reports/{filed.ReportId}/resolve",
            new { decision = "CONFIRMED" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        // The first decision stands: who decided is not overwritten by whoever arrived second.
        Assert.Equal(VehicleReportStatuses.Dismissed, (await harness.ReportsAsync()).Single().Status);
    }

    /// <summary>The moderation inbox admin-bff draws (SCR-AP-005).</summary>
    [Fact]
    public async Task The_queue_shows_pending_reports_and_drops_them_once_resolved()
    {
        await using var harness = await SafetyHarness.StartAsync(postgres, redis);

        var reporter = await harness.Seed.UserAsync();
        var driver = await harness.Seed.UserAsync(role: "driver");
        var vehicleId = await harness.Seed.VehicleAsync(driver.Id);
        var rideId = await harness.Seed.RideAsync(reporter.Id, driver.Id, vehicleId);

        var filed = await SafetyHarness.OkAsync<VehicleReportResponse>(
            await harness.PostAsync(
                "/v1/reports/vehicle",
                new { vehicleId, reason = "complaint", tripId = rideId },
                harness.Tokens.Passenger(reporter.Id)),
            "file report");

        using (var queued = await harness.InternalAsync(HttpMethod.Get, "/v1/internal/safety/reports/queue"))
        {
            var page = await SafetyHarness.OkAsync<CursorPageResponse<VehicleReportResponse>>(queued, "queue");
            Assert.Single(page.Items);
        }

        using (var resolved = await harness.InternalAsync(
                   HttpMethod.Post,
                   $"/v1/internal/safety/reports/{filed.ReportId}/resolve",
                   new { decision = "DISMISSED" }))
        {
            Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        }

        using var empty = await harness.InternalAsync(HttpMethod.Get, "/v1/internal/safety/reports/queue");
        var after = await SafetyHarness.OkAsync<CursorPageResponse<VehicleReportResponse>>(empty, "queue");

        Assert.Empty(after.Items);
    }

    /// <summary>The moderation plane sends nobody anywhere without the key.</summary>
    [Fact]
    public async Task The_internal_plane_refuses_a_caller_with_no_key()
    {
        await using var harness = await SafetyHarness.StartAsync(postgres, redis);

        using var response = await harness.InternalAsync(
            HttpMethod.Get, "/v1/internal/safety/reports/queue", apiKey: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// P-12's forensic read. The rows are ride-svc's to write; the question they exist to answer had
    /// nowhere to be asked.
    /// </summary>
    [Fact]
    public async Task The_location_request_audit_answers_the_p12_question()
    {
        await using var harness = await SafetyHarness.StartAsync(postgres, redis);

        var booker = await harness.Seed.UserAsync();

        await harness.Seed.LocationRequestAuditAsync(booker.Id, "Declined");
        await harness.Seed.LocationRequestAuditAsync(booker.Id, "Declined");
        await harness.Seed.LocationRequestAuditAsync(booker.Id, "Confirmed");
        await harness.Seed.LocationRequestAuditAsync(booker.Id, "NotRegistered");

        using var response = await harness.InternalAsync(
            HttpMethod.Get, $"/v1/internal/safety/location-requests/{booker.Id}");

        var page = await SafetyHarness.OkAsync<LocationRequestAuditPage>(response, "audit");

        Assert.Equal(booker.Id, page.BookerId);
        Assert.Equal(2, page.Totals["Declined"]);
        Assert.Equal(1, page.Totals["Confirmed"]);
        Assert.Equal(4, page.Items.Count);

        // The subject is a digest, never a number: the rider is frequently somebody with no account
        // (P-03), and the question is "how often was this booker declined".
        Assert.All(page.Items, item => Assert.DoesNotContain("+94", item.RiderPhoneFingerprint, StringComparison.Ordinal));
    }

    /// <summary>dispatch-svc's own query, run against the rows this service wrote.</summary>
    private static async Task<IReadOnlyList<Candidate>> NarrowAsync(
        SafetyHarness harness, Guid rideId, Guid passengerId, IReadOnlyCollection<Guid> driverIds)
    {
        // The service's own connection, because the query binds a GeoPoint and needs the kernel's
        // PostGIS type mapping.
        await using var connection = await harness.OpenServiceConnectionAsync();

        return await new CandidateRepository().NarrowAsync(
            connection,
            new CandidateQuery(
                rideId,
                passengerId,
                new GeoPoint(6.9271, 79.8612),
                "three_wheeler",
                RadiusM: 5_000,
                MaxPositionAge: TimeSpan.FromMinutes(5)),
            driverIds,
            CancellationToken.None);
    }
}
