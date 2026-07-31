using System.Net;
using Dapper;
using MageRide.Fleet.Documents;
using MageRide.Fleet.Domain;
using MageRide.Fleet.Endpoints;
using MageRide.Fleet.Tests.Infrastructure;
using MageRide.TestKit;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MageRide.Fleet.Tests.Integration;

/// <summary>
/// AL-50 / US-27.3 — SCR-FP-004's four named document slots, and the approval gate they hold.
/// </summary>
/// <remarks>
/// The C059 definition of done's first item — "a Mode A vehicle cannot reach APPROVED without a
/// verified route permit" — is
/// <see cref="A_mode_A_vehicle_cannot_be_approved_without_a_verified_route_permit"/>.
/// </remarks>
[Collection<FleetCollection>]
public sealed class VehicleDocumentTests(PostgresFixture postgres)
{
    [Fact]
    public async Task An_unfiled_vehicle_shows_four_slots_and_only_mode_A_requires_the_permit()
    {
        var ocr = new StubExtractionClient();
        await using var harness = await StartAsync(ocr);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        var bus = await AddAsync(harness, fleet, "WP-PA-1001", "bus", "A");
        var van = await AddAsync(harness, fleet, "WP-PB-1002", "van", "B");

        var busSlots = await harness.GetAsync<VehicleDocumentSlotsResponse>(
            $"/v1/fleets/{fleet.FleetId}/vehicles/{bus}/documents", fleet.OwnerBearer);

        // All four slots are rendered whatever the mode — SCR-FP-004 draws four boxes, and a Mode B
        // vehicle's permit box is an empty optional one rather than an absent one.
        Assert.Equal(4, busSlots.Items.Count);
        Assert.All(busSlots.Items, slot => Assert.Equal("missing", slot.Status));
        Assert.All(busSlots.Items, slot => Assert.True(slot.Required));

        var vanSlots = await harness.GetAsync<VehicleDocumentSlotsResponse>(
            $"/v1/fleets/{fleet.FleetId}/vehicles/{van}/documents", fleet.OwnerBearer);

        var permit = Assert.Single(vanSlots.Items, slot => slot.Kind == VehicleDocumentKinds.Permit);
        Assert.False(permit.Required);
        Assert.Equal(3, vanSlots.Items.Count(slot => slot.Required));
    }

    /// <summary>
    /// <b>Definition of done:</b> a Mode A vehicle cannot reach APPROVED without a verified route
    /// permit.
    /// </summary>
    [Fact]
    public async Task A_mode_A_vehicle_cannot_be_approved_without_a_verified_route_permit()
    {
        var ocr = new StubExtractionClient();
        await using var harness = await StartAsync(ocr);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        var bus = await AddAsync(harness, fleet, "WP-PC-2001", "bus", "A");

        // The three every mode needs, and no permit.
        foreach (var kind in new[] { "registration_copy", "insurance", "revenue_license" })
        {
            using var uploaded = await harness.UploadVehicleDocumentAsync(
                fleet.FleetId, bus, fleet.OwnerBearer, kind);

            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        }

        var partial = await harness.GetAsync<VehicleDocumentSlotsResponse>(
            $"/v1/fleets/{fleet.FleetId}/vehicles/{bus}/documents", fleet.OwnerBearer);

        Assert.Equal(3, partial.Items.Count(slot => slot.Status == "verified"));
        Assert.Equal(
            "missing",
            Assert.Single(partial.Items, slot => slot.Kind == VehicleDocumentKinds.Permit).Status);

        var officerId = await harness.CreateUserAsync("verification_officer");

        using var refused = await harness.InternalAsync(
            HttpMethod.Post,
            $"/v1/internal/fleets/{fleet.FleetId}/vehicles/{bus}/approve",
            new { officerId = officerId.ToString() });

        var problem = await FleetHarness.ProblemAsync(refused);

        Assert.Equal(HttpStatusCode.Conflict, problem.Status);
        Assert.Equal("documents-incomplete", problem.Code);
        Assert.Contains("permit", problem.Body, StringComparison.Ordinal);

        // Nothing was written on the way to the refusal.
        Assert.Equal("PENDING", await harness.VehicleStatusAsync(bus));

        // File the permit, and the same call succeeds.
        using var filed = await harness.UploadVehicleDocumentAsync(
            fleet.FleetId, bus, fleet.OwnerBearer, "route_permit");

        Assert.Equal(HttpStatusCode.Created, filed.StatusCode);

        var approved = await harness.InternalAsync<VehicleDecisionResponse>(
            HttpMethod.Post,
            $"/v1/internal/fleets/{fleet.FleetId}/vehicles/{bus}/approve",
            new { officerId = officerId.ToString() });

        Assert.Equal("APPROVED", approved.Vehicle.Status);
        Assert.Equal("docs_complete", approved.DocsStatus);
        Assert.Equal("APPROVED", await harness.VehicleStatusAsync(bus));

        // And a Mode B vehicle with the same three documents and no permit *is* approvable — the
        // permit is Mode A's requirement, not everybody's.
        var van = await AddAsync(harness, fleet, "WP-PD-2002", "van", "B");

        foreach (var kind in new[] { "registration_copy", "insurance", "revenue_license" })
        {
            using var uploaded = await harness.UploadVehicleDocumentAsync(
                fleet.FleetId, van, fleet.OwnerBearer, kind);

            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        }

        var vanDecision = await harness.InternalAsync<VehicleDecisionResponse>(
            HttpMethod.Post,
            $"/v1/internal/fleets/{fleet.FleetId}/vehicles/{van}/approve",
            new { officerId = officerId.ToString() });

        Assert.Equal("APPROVED", vanDecision.Vehicle.Status);
    }

    /// <summary>
    /// A document nothing could read holds its slot, which holds the vehicle (D5' §14.1a).
    /// </summary>
    [Fact]
    public async Task An_unread_document_is_pending_and_still_blocks_approval()
    {
        var ocr = new StubExtractionClient();
        ocr.Unreadable.Add(VehicleDocumentKinds.Insurance);

        await using var harness = await StartAsync(ocr);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        var van = await AddAsync(harness, fleet, "WP-PE-3001", "van", "B");

        foreach (var kind in new[] { "registration_copy", "insurance", "revenue_license" })
        {
            using var uploaded = await harness.UploadVehicleDocumentAsync(
                fleet.FleetId, van, fleet.OwnerBearer, kind);

            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        }

        var slots = await harness.GetAsync<VehicleDocumentSlotsResponse>(
            $"/v1/fleets/{fleet.FleetId}/vehicles/{van}/documents", fleet.OwnerBearer);

        var insurance = Assert.Single(slots.Items, slot => slot.Kind == VehicleDocumentKinds.Insurance);

        // The document is filed and its required field is a row the officer can fill, rather than
        // an absence they have to notice.
        Assert.Equal("pending", insurance.Status);
        Assert.NotNull(insurance.DocId);
        Assert.Contains(
            insurance.Fields,
            field => field.Key == VehicleDocumentFieldKeys.InsuranceExpiry
                && field.Value is null
                && field.VerifyStatus == "pending");

        var officerId = await harness.CreateUserAsync("verification_officer");

        using var refused = await harness.InternalAsync(
            HttpMethod.Post,
            $"/v1/internal/fleets/{fleet.FleetId}/vehicles/{van}/approve",
            new { officerId = officerId.ToString() });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    /// <summary>
    /// I-25.2: a plate that read as something else is a confident reading of the wrong vehicle.
    /// </summary>
    [Fact]
    public async Task A_registration_copy_whose_plate_does_not_match_is_pending_however_sure_the_extractor_was()
    {
        var ocr = new StubExtractionClient { RegNoMatch = "false", Confidence = 0.99m };
        await using var harness = await StartAsync(ocr);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        var van = await AddAsync(harness, fleet, "WP-PF-4001", "van", "B");

        using var uploaded = await harness.UploadVehicleDocumentAsync(
            fleet.FleetId, van, fleet.OwnerBearer, "registration_copy");

        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);

        var slots = await harness.GetAsync<VehicleDocumentSlotsResponse>(
            $"/v1/fleets/{fleet.FleetId}/vehicles/{van}/documents", fleet.OwnerBearer);

        Assert.Equal(
            "pending",
            Assert.Single(slots.Items, slot => slot.Kind == VehicleDocumentKinds.Registration).Status);
    }

    /// <summary>A re-upload supersedes rather than accompanies (registry-svc's "current" rule).</summary>
    [Fact]
    public async Task A_re_upload_supersedes_the_document_it_replaces()
    {
        var ocr = new StubExtractionClient();
        ocr.Unreadable.Add(VehicleDocumentKinds.RevenueLicense);

        await using var harness = await StartAsync(ocr);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        var van = await AddAsync(harness, fleet, "WP-PG-5001", "van", "B");

        using (var blurred = await harness.UploadVehicleDocumentAsync(
            fleet.FleetId, van, fleet.OwnerBearer, "revenue_license"))
        {
            Assert.Equal(HttpStatusCode.Created, blurred.StatusCode);
        }

        var pending = await harness.GetAsync<VehicleDocumentSlotsResponse>(
            $"/v1/fleets/{fleet.FleetId}/vehicles/{van}/documents", fleet.OwnerBearer);

        Assert.Equal(
            "pending",
            Assert.Single(pending.Items, slot => slot.Kind == VehicleDocumentKinds.RevenueLicense).Status);

        // The second attempt reads cleanly, and the operator is not held down by the first.
        ocr.Unreadable.Clear();

        using (var clear = await harness.UploadVehicleDocumentAsync(
            fleet.FleetId, van, fleet.OwnerBearer, "revenue_license"))
        {
            Assert.Equal(HttpStatusCode.Created, clear.StatusCode);
        }

        var verified = await harness.GetAsync<VehicleDocumentSlotsResponse>(
            $"/v1/fleets/{fleet.FleetId}/vehicles/{van}/documents", fleet.OwnerBearer);

        Assert.Equal(
            "verified",
            Assert.Single(verified.Items, slot => slot.Kind == VehicleDocumentKinds.RevenueLicense).Status);
    }

    /// <summary>A Mode B vehicle has no route permit to file, and is told so.</summary>
    [Fact]
    public async Task A_route_permit_on_a_mode_B_vehicle_is_refused()
    {
        var ocr = new StubExtractionClient();
        await using var harness = await StartAsync(ocr);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        var van = await AddAsync(harness, fleet, "WP-PH-6001", "van", "B");

        using var refused = await harness.UploadVehicleDocumentAsync(
            fleet.FleetId, van, fleet.OwnerBearer, "route_permit");

        var problem = await FleetHarness.ProblemAsync(refused);

        Assert.Equal(HttpStatusCode.BadRequest, problem.Status);
        Assert.Contains("Mode A", problem.Body, StringComparison.Ordinal);

        // Nothing was written — not even the upload row.
        Assert.Equal(0, ocr.Calls);
    }

    /// <summary>
    /// <c>ck_documents_owner</c> is an XOR: a fleet document names the fleet and never a driver.
    /// </summary>
    [Fact]
    public async Task A_fleet_document_carries_the_fleet_and_no_driver()
    {
        var ocr = new StubExtractionClient();
        await using var harness = await StartAsync(ocr);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        var van = await AddAsync(harness, fleet, "WP-PJ-7001", "van", "B");

        using var uploaded = await harness.UploadVehicleDocumentAsync(
            fleet.FleetId, van, fleet.OwnerBearer, "insurance");

        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);

        await using var connection = await harness.OpenAsync();

        var driverId = await connection.ExecuteScalarAsync<Guid?>(
            "SELECT driver_id FROM registry.documents WHERE vehicle_id = @Id;", new { Id = van });
        var documentFleet = await connection.ExecuteScalarAsync<Guid?>(
            "SELECT fleet_id FROM registry.documents WHERE vehicle_id = @Id;", new { Id = van });

        Assert.Null(driverId);
        Assert.Equal(fleet.FleetId, documentFleet);
    }

    /// <summary>A rejection needs a reason and does not need verified paperwork.</summary>
    [Fact]
    public async Task Rejection_is_ungated_and_carries_its_reason()
    {
        var ocr = new StubExtractionClient();
        await using var harness = await StartAsync(ocr);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        var bus = await AddAsync(harness, fleet, "WP-PK-8001", "bus", "A");
        var officerId = await harness.CreateUserAsync("verification_officer");

        using var noReason = await harness.InternalAsync(
            HttpMethod.Post,
            $"/v1/internal/fleets/{fleet.FleetId}/vehicles/{bus}/reject",
            new { officerId = officerId.ToString() });

        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        // No document has ever been filed, and the rejection still lands.
        var rejected = await harness.InternalAsync<VehicleDecisionResponse>(
            HttpMethod.Post,
            $"/v1/internal/fleets/{fleet.FleetId}/vehicles/{bus}/reject",
            new { officerId = officerId.ToString(), reason = "Insurance names a different vehicle." });

        Assert.Equal("REJECTED", rejected.Vehicle.Status);
        Assert.Equal("REJECTED", await harness.VehicleStatusAsync(bus));
    }

    private Task<FleetHarness> StartAsync(StubExtractionClient ocr) =>
        FleetHarness.StartAsync(
            postgres: postgres,
            configure: builder => builder.Services.TryAddSingleton<IVehicleDocumentExtractionClient>(ocr));

    private static async Task<Guid> AddAsync(
        FleetHarness harness, SeededFleet fleet, string registration, string type, string mode)
    {
        var vehicle = await harness.PostJsonAsync<FleetVehicleResponse>(
            $"/v1/fleets/{fleet.FleetId}/vehicles",
            new { registrationNumber = registration, vehicleType = type, mode },
            fleet.OwnerBearer);

        return Guid.Parse(vehicle.VehicleId);
    }
}
