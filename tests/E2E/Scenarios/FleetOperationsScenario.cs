using System.Globalization;
using System.Net;
using System.Text.Json;
using Dapper;
using MageRide.E2E.Infrastructure;
using MageRide.TcpAdapter.Protocols;
using MageRide.TestKit;

namespace MageRide.E2E.Scenarios;

/// <summary>
/// The Fleet Portal's operations half, end to end: the approval gate, AL-50's document slots, driver
/// assignment, the org-scoped map and analytics, and the cross-org read that the database refuses
/// (URD Epic 13, US-13.1 … US-13.9, AL-50).
/// </summary>
/// <remarks>
/// <para>
/// <b>The map and the analytics are drawn from real telemetry.</b> A bus in these scenarios has a
/// tracker bound through US-13.12, reports through tcp-adapter, and its fixes reach
/// <c>telemetry.positions</c> through the whole hot path — so "the operator's map shows their own
/// vehicles" is a claim about a join across two bounded contexts and eight services, rather than
/// about rows a fixture inserted.
/// </para>
/// <para>
/// <b>DoD 2 lives in <see cref="A_cross_org_read_is_refused_by_the_database"/>.</b> Every other
/// assertion in this file goes through fleet-svc, whose repositories carry their own
/// <c>WHERE fleet_id =</c>; that one connects as a real non-superuser login holding nothing but
/// <c>mageride_fleet_reader</c>'s grants, with no fleet-svc code in the path, because an assertion
/// made through this service's own SQL would pass just as happily if migrations 1806 and 1807's
/// policies did not exist.
/// </para>
/// </remarks>
[Collection<ModeAbCollection>]
[Trait("Category", "ModeAB")]
public sealed class FleetOperationsScenario(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda, EmqxFixture emqx)
    : ModeAbScenario(postgres, redis, redpanda, emqx)
{
    /// <summary>US-13.A7 — an unapproved organisation onboards nothing, and still sees what it runs.</summary>
    /// <remarks>
    /// The two halves are the point. Onboarding and assignment are disabled until a Verification
    /// Officer has read the KYC; monitoring is not, because a PENDING organisation waits days for
    /// that officer and must still be able to watch the vehicles it already has. The gate is held by
    /// the route <em>group</em> rather than by each handler, so a route added to either group is
    /// gated the moment it is mapped.
    /// </remarks>
    [Fact]
    public async Task An_unapproved_organisation_cannot_onboard_but_can_still_watch() =>
        await RunAsync(async (fleet, _) =>
        {
            var org = await fleet.CreateOrgAsync();
            var driver = await fleet.CreateDriverAsync();

            using (var response = await ModeAbFleet.GetAsync(
                fleet.FleetClient, $"/v1/fleets/{org.FleetId}", org.OwnerBearer))
            {
                await ModeAbFleet.AssertSuccessAsync(response, "reading the organisation");
                Assert.Equal("PENDING", (await ModeAbFleet.ReadJsonAsync(response)).GetProperty("status").GetString());
            }

            using (var refused = await ModeAbFleet.PostAsync(
                fleet.FleetClient,
                $"/v1/fleets/{org.FleetId}/vehicles",
                new { registrationNumber = "WP-PENDING-1", vehicleType = "bus", mode = "A" },
                org.OwnerBearer))
            {
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
                Assert.Equal("fleet-not-approved", await ModeAbFleet.ProblemCodeAsync(refused));
            }

            using (var refused = await ModeAbFleet.PostAsync(
                fleet.FleetClient,
                $"/v1/fleets/{org.FleetId}/assignments",
                new
                {
                    vehicleId = Guid.CreateVersion7().ToString(),
                    driverId = driver.DriverId.ToString(),
                    from = DateTimeOffset.UtcNow,
                },
                org.OwnerBearer))
            {
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            }

            // Monitoring is outside the gate, all three of them.
            foreach (var view in new[] { "map", "analytics", "alerts" })
            {
                using var watching = await ModeAbFleet.GetAsync(
                    fleet.FleetClient, $"/v1/fleets/{org.FleetId}/{view}", org.OwnerBearer);

                await ModeAbFleet.AssertSuccessAsync(watching, $"a PENDING organisation reading its {view}");
            }

            // And the officer's decision opens the gate, through the plane admin-bff calls.
            await fleet.ApproveOrgAsync(org.FleetId);

            var vehicle = await fleet.OnboardVehicleAsync(org, driver, approve: false);

            Assert.NotEqual(Guid.Empty, vehicle.VehicleId);
        });

    /// <summary>
    /// AL-50 — a required document slot holds a vehicle out of APPROVED, and the mode decides which
    /// slots are required.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registration, insurance and revenue licence for every vehicle; the route permit for Mode A as
    /// well, because Sri Lankan passenger transport legally requires one (US-27.3). A Mode B school
    /// van needs none, and uploading one is refused rather than accepted and ignored — an operator
    /// who filed a permit would otherwise watch a chip that never turns green on a slot nothing asks
    /// for.
    /// </para>
    /// <para>
    /// <b>The approval half of AL-50 is not reachable from this fleet, and that is a finding.</b> A
    /// slot is <c>verified</c> only when its document has fields and none of them is <c>pending</c>,
    /// and fleet-svc writes a field <c>auto_verified</c> only when ocr-svc returns it above the
    /// confidence threshold — which ocr-svc's on-prem path caps <em>below</em> by construction. So
    /// with no reachable Gemini every field is pending, and the only surface that can confirm one is
    /// admin-bff's. See <see cref="Unreachable"/>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_required_document_slot_holds_a_vehicle_out_of_approved() =>
        await RunAsync(async (fleet, vehicles) =>
        {
            var org = await fleet.CreateApprovedOrgAsync();
            var driver = await fleet.CreateDriverAsync();

            var bus = await fleet.OnboardVehicleAsync(org, driver, mode: "A", approve: false);
            var van = await fleet.OnboardVehicleAsync(org, await fleet.CreateDriverAsync(), mode: "B", vehicleType: "van", approve: false);

            vehicles.Add(bus.VehicleId);
            vehicles.Add(van.VehicleId);

            // Four slots either way; what differs is which of them are required.
            var busSlots = await ReadSlotsAsync(fleet, org, bus.VehicleId);
            var vanSlots = await ReadSlotsAsync(fleet, org, van.VehicleId);

            Assert.Equal(
                ["registration", "insurance", "revenue_license", "permit"],
                busSlots.Select(slot => slot.Kind).ToArray());

            Assert.Equal(
                ["registration", "insurance", "revenue_license", "permit"],
                busSlots.Where(slot => slot.Required).Select(slot => slot.Kind).ToArray());

            Assert.Equal(
                ["registration", "insurance", "revenue_license"],
                vanSlots.Where(slot => slot.Required).Select(slot => slot.Kind).ToArray());

            Assert.All(busSlots, slot => Assert.Equal("missing", slot.Status));

            // A permit belongs to a Mode A passenger-transport vehicle. On a van it is refused.
            using (var refused = await fleet.UploadVehicleDocumentAsync(org, van.VehicleId, "route_permit"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            }

            // File all four on the bus. Every one is stored and read by nobody, because ocr-svc is
            // not in this fleet — so every chip is `pending`, which is what an unread document is.
            foreach (var kind in new[] { "registration_copy", "insurance", "revenue_license", "route_permit" })
            {
                using var uploaded = await fleet.UploadVehicleDocumentAsync(
                    org, bus.VehicleId, kind, DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)));

                await ModeAbFleet.AssertSuccessAsync(uploaded, $"filing the {kind}");
            }

            var filed = await ReadSlotsAsync(fleet, org, bus.VehicleId);

            Assert.All(filed, slot => Assert.Equal("pending", slot.Status));
            Assert.All(filed, slot => Assert.NotNull(slot.DocId));

            // The gate, through the plane a Verification Officer's Approve reaches it on.
            using (var refused = await ModeAbFleet.PostInternalAsync(
                fleet.FleetClient,
                $"/v1/internal/fleets/{org.FleetId}/vehicles/{bus.VehicleId}/approve",
                new { officerId = Guid.CreateVersion7().ToString() },
                ModeAbFleet.FleetInternalKey))
            {
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
                Assert.Equal("documents-incomplete", await ModeAbFleet.ProblemCodeAsync(refused));

                // The refusal names what is outstanding, because the operator has to know what to
                // do next.
                var detail = await refused.Content.ReadAsStringAsync();

                Assert.Contains("registration", detail, StringComparison.Ordinal);
                Assert.Contains("permit", detail, StringComparison.Ordinal);
            }

            Assert.Equal("PENDING", await VehicleStatusAsync(fleet, bus.VehicleId));

            // And a vehicle nobody has approved cannot carry a journey: the eligibility projection
            // every go-live path reads is APPROVED plus an ACTIVE dispatch state.
            using var start = await ModeAbFleet.PostAsync(
                fleet.TripStateClient,
                "/v1/sessions/start",
                new { vehicleId = bus.VehicleId.ToString(), mode = "A" },
                driver.Bearer);

            Assert.Equal(HttpStatusCode.Forbidden, start.StatusCode);
            Assert.Equal("vehicle-not-approved", await ModeAbFleet.ProblemCodeAsync(start));
        });

    /// <summary>
    /// The AL-50 gap this fleet cannot close, asserted as still a gap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No route on any service in this fleet can move a fleet vehicle to APPROVED.</b> The chain
    /// is: the gate needs every required slot <c>verified</c> → a slot is verified only when its
    /// document has at least one field and none is <c>pending</c> → fleet-svc writes
    /// <c>auto_verified</c> only above <c>Fleet:OcrConfidenceThreshold</c> → ocr-svc's Tesseract path
    /// is capped below that threshold by its own option validator, so without a reachable Gemini
    /// every field it can produce is <c>pending</c>. The one surface that can confirm a field by
    /// hand is admin-bff's <c>PUT /v1/admin/verification/{subjectId}/fields/{fieldKey}</c> (C062).
    /// </para>
    /// <para>
    /// So this asserts the two halves that make the statement true today — a fully documented
    /// vehicle is still refused, and every field on it is <c>pending</c> — and it is written to fail
    /// the day somebody makes approval reachable, at which point it should be deleted and the
    /// positive path asserted in its place. A recorded gap nobody checks is a comment.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Unreachable() =>
        await RunAsync(async (fleet, vehicles) =>
        {
            var org = await fleet.CreateApprovedOrgAsync();
            var van = await fleet.OnboardVehicleAsync(
                org, await fleet.CreateDriverAsync(), mode: "B", vehicleType: "van", approve: false);

            vehicles.Add(van.VehicleId);

            foreach (var kind in new[] { "registration_copy", "insurance", "revenue_license" })
            {
                using var uploaded = await fleet.UploadVehicleDocumentAsync(
                    org, van.VehicleId, kind, DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)));

                await ModeAbFleet.AssertSuccessAsync(uploaded, $"filing the {kind}");
            }

            var slots = await ReadSlotsAsync(fleet, org, van.VehicleId);
            var required = slots.Where(slot => slot.Required).ToArray();

            Assert.Equal(3, required.Length);
            Assert.All(required, slot => Assert.Equal("pending", slot.Status));

            // Every field the platform could write is waiting on a person, and nothing here is that
            // person.
            Assert.All(
                required.SelectMany(slot => slot.Fields),
                field => Assert.Equal("pending", field.VerifyStatus));

            using var refused = await ModeAbFleet.PostInternalAsync(
                fleet.FleetClient,
                $"/v1/internal/fleets/{org.FleetId}/vehicles/{van.VehicleId}/approve",
                new { officerId = Guid.CreateVersion7().ToString() },
                ModeAbFleet.FleetInternalKey);

            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("documents-incomplete", await ModeAbFleet.ProblemCodeAsync(refused));
        });

    /// <summary>US-13.2 and US-13.9 — an assignment is what lets a driver take the bus out, and revoking it stops them.</summary>
    /// <remarks>
    /// <b>"Auto-expires" is a predicate, not a sweep.</b> <c>registry.driver_eligible_vehicles</c>
    /// evaluates the assignment's window at read time, so the driver's app stops offering the
    /// vehicle the instant the window closes with the row untouched and nobody pressing anything —
    /// and a revoke takes effect on the next request rather than on the next sweep. This drives the
    /// revoke, which is the half a scenario can observe without waiting out a window.
    /// </remarks>
    [Fact]
    public async Task An_assignment_is_what_lets_a_driver_take_the_bus_out() =>
        await RunAsync(async (fleet, vehicles) =>
        {
            var org = await fleet.CreateApprovedOrgAsync();
            var driver = await fleet.CreateDriverAsync();
            var relief = await fleet.CreateDriverAsync();

            var bus = await fleet.OnboardVehicleAsync(org, driver);
            vehicles.Add(bus.VehicleId);

            // The relief driver has no assignment, so the vehicle does not exist as far as they are
            // concerned. 404 and not 403: the projection is driver-scoped, so "not yours" and "does
            // not exist" are the same query result and telling them apart leaks a stranger's bus.
            using (var refused = await ModeAbFleet.PostAsync(
                fleet.TripStateClient,
                "/v1/sessions/start",
                new { vehicleId = bus.VehicleId.ToString(), mode = "A" },
                relief.Bearer))
            {
                Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
            }

            // The manager books them for Thursday's shift — a window that has not opened yet.
            var thursday = await fleet.AssignDriverAsync(
                org, bus.VehicleId, relief,
                validFrom: DateTimeOffset.UtcNow.AddDays(3),
                expiresAt: DateTimeOffset.UtcNow.AddDays(4));

            Assert.NotEqual(Guid.Empty, thursday);

            using (var tooEarly = await ModeAbFleet.PostAsync(
                fleet.TripStateClient,
                "/v1/sessions/start",
                new { vehicleId = bus.VehicleId.ToString(), mode = "A" },
                relief.Bearer))
            {
                Assert.Equal(HttpStatusCode.NotFound, tooEarly.StatusCode);
            }

            // The assigned driver, whose window is open, can.
            var live = await fleet.StartJourneyAsync(bus);

            using (var ended = await fleet.EndJourneyAsync(bus, live.SessionId))
            {
                await ModeAbFleet.AssertSuccessAsync(ended, "End Journey");
            }

            // The roster read the portal renders, and the assignment it lists.
            using (var listed = await ModeAbFleet.GetAsync(
                fleet.FleetClient, $"/v1/fleets/{org.FleetId}/assignments", org.OwnerBearer))
            {
                await ModeAbFleet.AssertSuccessAsync(listed, "listing the org's assignments");

                var items = (await ModeAbFleet.ReadJsonAsync(listed)).GetProperty("items");

                Assert.Equal(2, items.GetArrayLength());
                Assert.Contains(
                    items.EnumerateArray(),
                    item => item.GetProperty("driverId").GetString() == driver.DriverId.ToString()
                        && item.GetProperty("active").GetBoolean());
            }

            // US-13.7/13.9's "immediately": the assignment is revoked and the next request is refused.
            var assignments = await AssignmentIdsAsync(fleet, org.FleetId, driver.DriverId);

            using (var revoked = await ModeAbFleet.DeleteAsync(
                fleet.FleetClient, $"/v1/fleets/{org.FleetId}/assignments/{assignments[0]}", org.OwnerBearer))
            {
                await ModeAbFleet.AssertSuccessAsync(revoked, "revoking the assignment");
            }

            using var afterwards = await ModeAbFleet.PostAsync(
                fleet.TripStateClient,
                "/v1/sessions/start",
                new { vehicleId = bus.VehicleId.ToString(), mode = "A" },
                driver.Bearer);

            Assert.Equal(HttpStatusCode.NotFound, afterwards.StatusCode);
        });

    /// <summary>US-13.3 and US-13.4 — the map and the analytics show the operator's own vehicles and nobody else's.</summary>
    /// <remarks>
    /// Two organisations, two buses, two trackers, one platform. Each operator's map is drawn from
    /// <c>telemetry.positions</c> through <c>_fleet</c> views scoped by the request's organisation,
    /// and the fixes in it were decoded from real GT06 frames minutes earlier.
    /// </remarks>
    [Fact]
    public async Task The_map_and_the_analytics_are_scoped_to_the_operators_own_vehicles() =>
        await RunAsync(async (fleet, vehicles) =>
        {
            var mine = await ArriveAsync(fleet, vehicles);
            var theirs = await ArriveAsync(fleet, vehicles);

            await using var myDevice = await TrackerDevice.ConnectAsync(fleet, ProtocolFamily.Gt06, mine.Imei);
            await using var theirDevice = await TrackerDevice.ConnectAsync(fleet, ProtocolFamily.Gt06, theirs.Imei);

            var myFix = await myDevice.ReportAsync(mine.Depot);
            var theirFix = await theirDevice.ReportAsync(theirs.Depot);

            await fleet.WaitForTelemetryAsync(mine.VehicleId, new ReportedFix(mine.Depot, myFix));
            await fleet.WaitForTelemetryAsync(theirs.VehicleId, new ReportedFix(theirs.Depot, theirFix));

            using (var map = await ModeAbFleet.GetAsync(
                fleet.FleetClient, $"/v1/fleets/{mine.Org.FleetId}/map", mine.Org.OwnerBearer))
            {
                await ModeAbFleet.AssertSuccessAsync(map, "reading the fleet map");

                var drawn = (await ModeAbFleet.ReadJsonAsync(map)).GetProperty("vehicles")
                    .EnumerateArray()
                    .Select(vehicle => vehicle.GetProperty("vehicleId").GetString())
                    .ToArray();

                Assert.Contains(mine.VehicleId.ToString(), drawn);
                Assert.DoesNotContain(theirs.VehicleId.ToString(), drawn);
            }

            using (var analytics = await ModeAbFleet.GetAsync(
                fleet.FleetClient,
                $"/v1/fleets/{mine.Org.FleetId}/analytics?from={DateTime.UtcNow.AddDays(-1):yyyy-MM-dd}"
                + $"&to={DateTime.UtcNow.AddDays(1):yyyy-MM-dd}",
                mine.Org.OwnerBearer))
            {
                await ModeAbFleet.AssertSuccessAsync(analytics, "reading the fleet analytics");

                var rows = (await ModeAbFleet.ReadJsonAsync(analytics)).GetProperty("items")
                    .EnumerateArray()
                    .Select(row => row.GetProperty("vehicleId").GetString())
                    .ToArray();

                Assert.DoesNotContain(theirs.VehicleId.ToString(), rows);

                // `earningsMinor` is absent rather than zero: a fleet's Mode A/B vehicles take no
                // fares on this platform, and zero would be a claim that the operator earned nothing.
                foreach (var row in (await ModeAbFleet.ReadJsonAsync(analytics)).GetProperty("items").EnumerateArray())
                {
                    Assert.True(
                        !row.TryGetProperty("earningsMinor", out var earnings)
                        || earnings.ValueKind == JsonValueKind.Null,
                        "the analytics claimed a Mode A/B vehicle earned a fare");
                }
            }

            // And an Owner of one organisation arriving at another's path gets nowhere, whatever
            // their token says: the membership row is the authority, not the claim.
            using var trespass = await ModeAbFleet.GetAsync(
                fleet.FleetClient, $"/v1/fleets/{theirs.Org.FleetId}/map", mine.Org.OwnerBearer);

            Assert.Equal(HttpStatusCode.Forbidden, trespass.StatusCode);
            Assert.Equal("not-fleet-member", await ModeAbFleet.ProblemCodeAsync(trespass));
        });

    /// <summary>
    /// C121 DoD 2 — a cross-org read is refused by Postgres, not by an application <c>WHERE</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The connection here holds nothing but the grants migrations 1806 and 1807 give
    /// <c>mageride_fleet_reader</c>, and there is no fleet-svc code in the path. The container's own
    /// <c>mageride</c> user is a superuser and a superuser bypasses RLS entirely, which is why the
    /// distinction between the two connections is the whole test.
    /// </para>
    /// <para>
    /// Both directions are asserted, and the second is what makes the design safe rather than merely
    /// correct: a scoped reader cannot see another organisation's rows <b>even by primary key</b>,
    /// and an <em>unscoped</em> one sees nothing at all — so a bug that forgot to set
    /// <c>app.fleet_id</c> returns an empty page rather than the platform.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_cross_org_read_is_refused_by_the_database() =>
        await RunAsync(async (fleet, vehicles) =>
        {
            var mine = await ArriveAsync(fleet, vehicles);
            var theirs = await ArriveAsync(fleet, vehicles);

            await using var reader = await fleet.OpenAsFleetReaderAsync();
            await using var transaction = await reader.BeginTransactionAsync();

            await reader.ExecuteAsync(
                new CommandDefinition(
                    "SET LOCAL ROLE mageride_fleet_reader; SELECT set_config('app.fleet_id', @FleetId, true);",
                    new { FleetId = mine.Org.FleetId.ToString() },
                    transaction));

            // Named by primary key, which is the strongest form of the question.
            var theirOrg = await reader.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*)::int FROM registry.fleets WHERE id = @Id;",
                new { Id = theirs.Org.FleetId },
                transaction));

            var theirRoster = await reader.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*)::int FROM registry.fleet_vehicles_fleet WHERE vehicle_id = @Id;",
                new { Id = theirs.VehicleId },
                transaction));

            var theirTeam = await reader.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*)::int FROM iam.fleet_members WHERE fleet_id = @Id;",
                new { Id = theirs.Org.FleetId },
                transaction));

            var theirJourneys = await reader.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*)::int FROM trips.sessions_fleet WHERE vehicle_id = @Id;",
                new { Id = theirs.VehicleId },
                transaction));

            Assert.Equal(0, theirOrg);
            Assert.Equal(0, theirRoster);
            Assert.Equal(0, theirTeam);
            Assert.Equal(0, theirJourneys);

            // The caller's own organisation is visible, so the zeroes above are scoping rather than
            // a broken connection.
            var ownOrg = await reader.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*)::int FROM registry.fleets WHERE id = @Id;",
                new { Id = mine.Org.FleetId },
                transaction));

            var ownRoster = await reader.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*)::int FROM registry.fleet_vehicles_fleet WHERE vehicle_id = @Id;",
                new { Id = mine.VehicleId },
                transaction));

            Assert.Equal(1, ownOrg);
            Assert.Equal(1, ownRoster);

            await transaction.RollbackAsync();

            // And the same reader with nothing scoped sees nothing at all. 1806 uses the
            // two-argument `current_setting` for exactly this: the one-argument form raises, and a
            // caller who catches the error is one retry away from an unscoped read.
            await using var unscoped = await fleet.OpenAsFleetReaderAsync();
            await using var plain = await unscoped.BeginTransactionAsync();

            await unscoped.ExecuteAsync(new CommandDefinition("SET LOCAL ROLE mageride_fleet_reader;", transaction: plain));

            Assert.Equal(
                0,
                await unscoped.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT count(*)::int FROM registry.fleets;", transaction: plain)));

            await plain.RollbackAsync();
        });

    // -------------------------------------------------------------------------------------------
    // Reads these scenarios share
    // -------------------------------------------------------------------------------------------

    private static async Task<IReadOnlyList<DocumentSlot>> ReadSlotsAsync(
        ModeAbFleet fleet, FleetOrg org, Guid vehicleId)
    {
        using var response = await ModeAbFleet.GetAsync(
            fleet.FleetClient, $"/v1/fleets/{org.FleetId}/vehicles/{vehicleId}/documents", org.OwnerBearer);

        await ModeAbFleet.AssertSuccessAsync(response, $"reading vehicle {vehicleId}'s document slots");

        return
        [
            .. (await ModeAbFleet.ReadJsonAsync(response)).GetProperty("items").EnumerateArray().Select(slot =>
                new DocumentSlot(
                    slot.GetProperty("kind").GetString()!,
                    slot.GetProperty("status").GetString()!,
                    slot.GetProperty("required").GetBoolean(),
                    slot.TryGetProperty("docId", out var id) && id.ValueKind == JsonValueKind.String
                        ? id.GetString()
                        : null,
                    [
                        .. slot.GetProperty("fields").EnumerateArray().Select(field =>
                            new ExtractedField(
                                field.GetProperty("key").GetString()!,
                                field.GetProperty("verifyStatus").GetString()!)),
                    ])),
        ];
    }

    private static async Task<string> VehicleStatusAsync(ModeAbFleet fleet, Guid vehicleId)
    {
        await using var connection = await fleet.OpenAsync();

        return await connection.ExecuteScalarAsync<string>(
            "SELECT status FROM registry.vehicles WHERE id = @Id;", new { Id = vehicleId }) ?? string.Empty;
    }

    private static async Task<IReadOnlyList<Guid>> AssignmentIdsAsync(ModeAbFleet fleet, Guid fleetId, Guid driverId)
    {
        await using var connection = await fleet.OpenAsync();

        return [.. await connection.QueryAsync<Guid>(
            """
            SELECT id FROM registry.fleet_assignments
             WHERE fleet_id = @FleetId AND driver_id = @DriverId AND revoked_at IS NULL
             ORDER BY assigned_at;
            """,
            new { FleetId = fleetId, DriverId = driverId })];
    }

    /// <summary>One of SCR-FP-004's chips, as the portal reads it.</summary>
    private sealed record DocumentSlot(
        string Kind, string Status, bool Required, string? DocId, IReadOnlyList<ExtractedField> Fields);

    /// <summary>One extracted field and AL-29's verdict on it.</summary>
    private sealed record ExtractedField(string Key, string VerifyStatus);
}
