using System.Net;
using System.Text.Json;
using Dapper;
using MageRide.Registry.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Registry.Tests.Integration;

/// <summary>
/// The AL-30 four-step machine: per-step persistence, the resume rule, the pending-review verdicts
/// and AL-27's auto-approval. This class is where every item of C029's definition of done except
/// the E-03 one is stated.
/// </summary>
[Collection<PostgresCollection>]
public sealed class VehicleOnboardingTests(PostgresFixture postgres)
{
    /// <summary>
    /// DoD item 5, and BR-25.4's first half: registration saves Step 1/4, so a vehicle nobody has
    /// finished is Incomplete and says which screen to open.
    /// </summary>
    [Fact]
    public async Task A_fresh_registration_has_one_saved_step_and_names_the_next_one()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        var registered = await harness.RegisterVehicleAsync(bearer);

        Assert.Equal("incomplete", registered.GetProperty("onboardingStatus").GetString());
        Assert.Equal("insurance", registered.GetProperty("nextStep").GetString());

        // D5' §14.1a marks vehicle details "(entered)": the type and plate this request carried
        // ARE the details step, so the wizard opens at 2/4 rather than asking for them again.
        var verification = registered.GetProperty("verification");
        Assert.Equal("VERIFIED", verification.GetProperty("vehicleDetails").GetString());
        Assert.Equal("PENDING_INPUT", verification.GetProperty("insurance").GetString());
        Assert.Equal("PENDING_INPUT", verification.GetProperty("revenueLicense").GetString());
        Assert.Equal("PENDING_INPUT", verification.GetProperty("photos").GetString());

        await using var connection = await harness.OpenAsync();

        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM registry.onboarding_steps WHERE vehicle_id = @Id;",
                new { Id = Guid.Parse(registered.GetProperty("vehicleId").GetString()!) }));
    }

    /// <summary>DoD item 3: all four verified flips the vehicle to APPROVED with no human action.</summary>
    [Fact]
    public async Task All_four_steps_verified_auto_approves_the_vehicle()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        var vehicleId = (await harness.RegisterVehicleAsync(bearer)).GetProperty("vehicleId").GetString()!;

        var last = await harness.CompleteOnboardingAsync(driverId, bearer, vehicleId);

        // Nobody approved this. No officer route was called and the dev seed path was not used —
        // the fourth verified step did it (AL-27, user decision 6/22).
        Assert.Equal("VERIFIED", last.GetProperty("stepStatus").GetString());
        Assert.Equal("approved", last.GetProperty("onboardingStatus").GetString());
        Assert.Equal("APPROVED", last.GetProperty("status").GetString());
        Assert.Null(last.GetProperty("nextStep").GetString());

        var status = await harness.GetAsync($"/v1/vehicles/{vehicleId}/onboarding-status", bearer);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);

        var state = await RegistryHarness.ReadJsonAsync(status);
        Assert.Equal("APPROVED", state.GetProperty("status").GetString());

        foreach (var step in new[] { "details", "insurance", "revenue", "photos" })
        {
            Assert.Equal("VERIFIED", state.GetProperty("steps").GetProperty(step).GetString());
        }

        // US-2.14's REGISTRATION_RESULT push needs a trigger, and this is it.
        var approval = Assert.Single(
            await harness.OutboxAsync(Guid.Parse(vehicleId)), e => e.EventType == "vehicle.approved");

        using var payload = JsonDocument.Parse(approval.Payload);
        Assert.Equal("auto", payload.RootElement.GetProperty("approvedBy").GetString());

        // And the vehicle is now go-live eligible without anything else happening (US-9.6).
        var mine = await RegistryHarness.ReadJsonAsync(await harness.GetAsync("/v1/vehicles/mine", bearer));
        Assert.True(mine.GetProperty("items")[0].GetProperty("isGoLiveEligible").GetBoolean());
    }

    /// <summary>DoD item 1, the low-confidence half (BR-25.2/BR-25.3).</summary>
    [Fact]
    public async Task A_low_confidence_field_sets_its_step_pending_review_and_flags_it()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = (await harness.RegisterVehicleAsync(bearer)).GetProperty("vehicleId").GetString()!;

        harness.Ocr.ReadDoubtfully("insurance");

        var response = await harness.SaveStepAsync(driverId, bearer, vehicleId, "insurance");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await RegistryHarness.ReadJsonAsync(response);
        Assert.Equal("PENDING_REVIEW", body.GetProperty("stepStatus").GetString());
        Assert.Equal("incomplete", body.GetProperty("onboardingStatus").GetString());

        // The driver is not blocked — BR-25.2 lets them proceed — but the resume point stays here
        // until somebody confirms the field.
        Assert.Equal("insurance", body.GetProperty("nextStep").GetString());

        await using var connection = await harness.OpenAsync();

        Assert.Equal(
            "pending",
            await connection.QuerySingleAsync<string>(
                """
                SELECT f.verify_status FROM registry.document_fields f
                  JOIN registry.documents d ON d.id = f.document_id
                 WHERE d.vehicle_id = @Id AND f.field_key = 'insurance_expiry';
                """,
                new { Id = Guid.Parse(vehicleId) }));

        // Flagged for admin verify (US-2.10, SCR-AP-003).
        var review = Assert.Single(
            await harness.OutboxAsync(Guid.Parse(vehicleId)), e => e.EventType == "document.review_required");

        using var payload = JsonDocument.Parse(review.Payload);
        Assert.Equal("insurance", payload.RootElement.GetProperty("step").GetString());
    }

    /// <summary>DoD item 1, the manual half: a driver-typed field is never silently trusted.</summary>
    [Fact]
    public async Task A_driver_typed_correction_sets_its_step_pending_review()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = (await harness.RegisterVehicleAsync(bearer)).GetProperty("vehicleId").GetString()!;

        // ocr-svc read the certificate perfectly. The driver disagreed and typed a date, which is
        // exactly the case BR-25.2 will not auto-verify however good the scan was.
        var response = await harness.SaveStepAsync(
            driverId,
            bearer,
            vehicleId,
            "insurance",
            new { fields = new Dictionary<string, string> { ["insurance_expiry"] = "2027-12-31" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "PENDING_REVIEW",
            (await RegistryHarness.ReadJsonAsync(response)).GetProperty("stepStatus").GetString());

        await using var connection = await harness.OpenAsync();

        var field = await connection.QuerySingleAsync<(string Source, string VerifyStatus, decimal? Confidence)>(
            """
            SELECT f.source, f.verify_status, f.confidence FROM registry.document_fields f
              JOIN registry.documents d ON d.id = f.document_id
             WHERE d.vehicle_id = @Id AND f.field_key = 'insurance_expiry';
            """,
            new { Id = Guid.Parse(vehicleId) });

        Assert.Equal("manual", field.Source);
        Assert.Equal("pending", field.VerifyStatus);
        Assert.Null(field.Confidence);

        // The typed value is still what the document expires on — pending means unverified, not
        // ignored, so E-03 has a date to sweep.
        Assert.Equal(
            new DateTimeOffset(2027, 12, 31, 0, 0, 0, TimeSpan.Zero).Date,
            (await harness.DocumentsAsync(Guid.Parse(vehicleId))).Single().ExpiresAt!.Value.UtcDateTime.Date);
    }

    /// <summary>DoD item 2: a plate OCR that does not match sets the photos step pending_review.</summary>
    [Fact]
    public async Task A_plate_that_does_not_match_the_registration_sets_photos_pending_review()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = (await harness.RegisterVehicleAsync(bearer)).GetProperty("vehicleId").GetString()!;

        Assert.Equal(HttpStatusCode.OK, (await harness.SaveStepAsync(driverId, bearer, vehicleId, "insurance")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await harness.SaveStepAsync(driverId, bearer, vehicleId, "revenue")).StatusCode);

        harness.Ocr.MisreadPlateAs("WP-ZZ-9999");

        var response = await harness.SaveStepAsync(driverId, bearer, vehicleId, "photos");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await RegistryHarness.ReadJsonAsync(response);
        Assert.Equal("PENDING_REVIEW", body.GetProperty("stepStatus").GetString());

        // Three steps verified and one mismatched is NOT an approval. This is the case the photos
        // step exists for: papers that belong to a different vehicle.
        Assert.Equal("PENDING", body.GetProperty("status").GetString());
        Assert.Equal("incomplete", body.GetProperty("onboardingStatus").GetString());

        await using var connection = await harness.OpenAsync();

        var matches = await connection.QueryAsync<(string Value, string VerifyStatus)>(
            """
            SELECT f.field_value, f.verify_status FROM registry.document_fields f
              JOIN registry.documents d ON d.id = f.document_id
             WHERE d.vehicle_id = @Id AND f.field_key = 'reg_no_match';
            """,
            new { Id = Guid.Parse(vehicleId) });

        // Both photos, and both pending: a confident reading of the wrong plate is not a
        // confidence problem, so the verdict does not depend on the score.
        Assert.Equal(2, matches.Count());
        Assert.All(matches, match =>
        {
            Assert.Equal("false", match.Value);
            Assert.Equal("pending", match.VerifyStatus);
        });
    }

    /// <summary>
    /// The AL-30 fence: resuming opens the first step that is not verified, never Step 1.
    /// </summary>
    [Fact]
    public async Task Resuming_opens_the_first_unverified_step_and_never_step_one()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = (await harness.RegisterVehicleAsync(bearer)).GetProperty("vehicleId").GetString()!;

        Assert.Equal(HttpStatusCode.OK, (await harness.SaveStepAsync(driverId, bearer, vehicleId, "insurance")).StatusCode);

        // Step 3 fails to extract. Step 2 verified, so the wizard must skip past it.
        harness.Ocr.FailEverything();
        Assert.Equal(HttpStatusCode.OK, (await harness.SaveStepAsync(driverId, bearer, vehicleId, "revenue")).StatusCode);

        var stalled = await RegistryHarness.ReadJsonAsync(
            await harness.GetAsync($"/v1/vehicles/{vehicleId}/onboarding-status", bearer));

        Assert.Equal("revenue", stalled.GetProperty("nextStep").GetString());
        Assert.Equal("VERIFIED", stalled.GetProperty("steps").GetProperty("details").GetString());
        Assert.Equal("VERIFIED", stalled.GetProperty("steps").GetProperty("insurance").GetString());
        Assert.Equal("PENDING_REVIEW", stalled.GetProperty("steps").GetProperty("revenue").GetString());

        // Re-uploading a legible one supersedes the failed attempt rather than being blocked by it.
        harness.Ocr.Responder = null;
        Assert.Equal(HttpStatusCode.OK, (await harness.SaveStepAsync(driverId, bearer, vehicleId, "revenue")).StatusCode);

        var resumed = await RegistryHarness.ReadJsonAsync(
            await harness.GetAsync($"/v1/vehicles/{vehicleId}/onboarding-status", bearer));

        Assert.Equal("photos", resumed.GetProperty("nextStep").GetString());
        Assert.Equal("VERIFIED", resumed.GetProperty("steps").GetProperty("revenue").GetString());
    }

    /// <summary>
    /// C054's fence, from registry-svc's side: an extractor that is down must not stop a driver
    /// saving their step (D5' §14.1a routes the document to an officer instead).
    /// </summary>
    [Fact]
    public async Task With_ocr_unavailable_the_step_still_saves_and_goes_to_review()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = (await harness.RegisterVehicleAsync(bearer)).GetProperty("vehicleId").GetString()!;

        harness.Ocr.FailEverything();

        var response = await harness.SaveStepAsync(driverId, bearer, vehicleId, "insurance");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "PENDING_REVIEW",
            (await RegistryHarness.ReadJsonAsync(response)).GetProperty("stepStatus").GetString());

        var state = await RegistryHarness.ReadJsonAsync(
            await harness.GetAsync($"/v1/vehicles/{vehicleId}/onboarding-status", bearer));

        // A required field that nothing read is written anyway, with a null value and pending, so
        // the officer sees "insurance expiry could not be read" as a row to fill rather than as an
        // absence they have to notice.
        var expiry = Assert.Single(
            state.GetProperty("fields").EnumerateArray().ToArray(),
            field => field.GetProperty("key").GetString() == "insurance_expiry");

        Assert.Equal(JsonValueKind.Null, expiry.GetProperty("value").ValueKind);
        Assert.Equal("pending", expiry.GetProperty("verifyStatus").GetString());
    }

    /// <summary>
    /// Editing the plate after the photos verified must send them back: otherwise a vehicle could
    /// be approved with front and back photos of a different registration.
    /// </summary>
    [Fact]
    public async Task Changing_the_registration_number_sends_verified_photos_back_for_review()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = (await harness.RegisterVehicleAsync(bearer)).GetProperty("vehicleId").GetString()!;

        await harness.CompleteOnboardingAsync(driverId, bearer, vehicleId);

        var response = await harness.SaveStepAsync(
            driverId, bearer, vehicleId, "details", new { registrationNumber = RegistryHarness.NextPlate() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await RegistryHarness.ReadJsonAsync(response);
        Assert.Equal("VERIFIED", body.GetProperty("stepStatus").GetString());

        // The details step itself is fine; the photos are now of the wrong plate.
        Assert.Equal("photos", body.GetProperty("nextStep").GetString());
        Assert.Equal("incomplete", body.GetProperty("onboardingStatus").GetString());

        // The registration is NOT un-approved. A Verification Officer's APPROVED is not overturned
        // by a derived verdict; My Vehicles shows Incomplete and the driver re-photographs.
        Assert.Equal("APPROVED", body.GetProperty("status").GetString());
    }

    /// <summary>
    /// AL-30's "Approve unlocks only when all Pending fields are confirmed", and the seam C062
    /// needs to say so.
    /// </summary>
    [Fact]
    public async Task Confirming_the_last_pending_field_lets_the_recompute_approve()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var officerId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = (await harness.RegisterVehicleAsync(bearer)).GetProperty("vehicleId").GetString()!;

        // The document kind, not the step name: ocr-svc is told what it is reading, and the
        // revenue step uploads a `revenue_license`.
        harness.Ocr.ReadDoubtfully("revenue_license");
        await harness.CompleteOnboardingAsync(driverId, bearer, vehicleId);

        var stalled = await RegistryHarness.ReadJsonAsync(
            await harness.GetAsync($"/v1/vehicles/{vehicleId}/onboarding-status", bearer));

        Assert.Equal("PENDING", stalled.GetProperty("status").GetString());
        Assert.Equal("revenue", stalled.GetProperty("nextStep").GetString());

        Assert.Equal(2, await harness.ConfirmPendingFieldsAsync(Guid.Parse(vehicleId), officerId));

        var recomputed = await harness.PostInternalAsync($"/v1/internal/vehicles/{vehicleId}/onboarding/recompute", null);
        Assert.Equal(HttpStatusCode.OK, recomputed.StatusCode);

        var state = await RegistryHarness.ReadJsonAsync(recomputed);

        // A confirmed field counts as verified for AL-30, so the approval that was waiting on the
        // officer happens the moment they finish.
        Assert.Equal("APPROVED", state.GetProperty("status").GetString());
        Assert.Equal("approved", state.GetProperty("onboardingStatus").GetString());
        Assert.Null(state.GetProperty("nextStep").GetString());
    }

    /// <summary>The AL-27 fence: in-app vehicle onboarding is Mode C, and only Mode C.</summary>
    [Fact]
    public async Task A_mode_b_vehicle_cannot_be_onboarded_in_the_driver_app()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var vehicleId = await harness.SeedFleetVehicleAsync(driverId, status: "PENDING");

        var response = await harness.SaveStepAsync(
            driverId, harness.Tokens.Driver(driverId), vehicleId.ToString(), "insurance");

        // Mode A/B vehicles and their permits are the Fleet Portal's (SCR-FP-004, AL-50).
        await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "mode-not-allowed");
    }

    [Fact]
    public async Task Another_driver_cannot_save_a_step_on_this_vehicle()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var ownerId = await harness.CreateDriverAsync();
        var strangerId = await harness.CreateDriverAsync();

        var vehicleId = (await harness.RegisterVehicleAsync(harness.Tokens.Driver(ownerId)))
            .GetProperty("vehicleId").GetString()!;

        var response = await harness.SaveStepAsync(
            strangerId, harness.Tokens.Driver(strangerId), vehicleId, "insurance");

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "not-owner");
    }

    [Fact]
    public async Task An_unknown_step_names_the_four_that_exist()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = (await harness.RegisterVehicleAsync(bearer)).GetProperty("vehicleId").GetString()!;

        var response = await harness.PutAsync($"/v1/vehicles/{vehicleId}/onboarding/permit", new { }, bearer);

        var problem = await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
        Assert.Contains("photos", problem.Root.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_photos_step_needs_both_sides()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = (await harness.RegisterVehicleAsync(bearer)).GetProperty("vehicleId").GetString()!;

        // One photo cannot show a vehicle's front and back plates at once (D5' §14.1a, step 4).
        var response = await harness.PutAsync(
            $"/v1/vehicles/{vehicleId}/onboarding/photos",
            new { fileId = await harness.SeedUploadAsync(driverId, "photos") },
            bearer);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    /// <summary>
    /// D3's one-shot registration: the four file ids the contract declares are honoured when sent,
    /// and the vehicle comes back approved from the single call.
    /// </summary>
    [Fact]
    public async Task Registering_with_all_four_documents_onboards_in_one_call()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        var response = await harness.PostAsync(
            "/v1/vehicles",
            new
            {
                registrationNumber = RegistryHarness.NextPlate(),
                vehicleType = "three_wheeler",
                mode = "C",
                driverName = "Nimal Perera",
                insuranceFileId = await harness.SeedUploadAsync(driverId, "insurance"),
                revenueLicenseFileId = await harness.SeedUploadAsync(driverId, "revenue_license"),
                vehiclePhotoFrontFileId = await harness.SeedUploadAsync(driverId, "vehicle_photo"),
                vehiclePhotoBackFileId = await harness.SeedUploadAsync(driverId, "vehicle_photo"),
            },
            bearer);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await RegistryHarness.ReadJsonAsync(response);
        Assert.Equal("APPROVED", body.GetProperty("status").GetString());
        Assert.Equal("approved", body.GetProperty("onboardingStatus").GetString());
        Assert.Null(body.GetProperty("nextStep").GetString());

        var verification = body.GetProperty("verification");
        foreach (var step in new[] { "vehicleDetails", "insurance", "revenueLicense", "photos" })
        {
            Assert.Equal("VERIFIED", verification.GetProperty(step).GetString());
        }
    }

    /// <summary>
    /// A bad file id on the registration body must be caught before the vehicle exists — otherwise
    /// the failed call leaves a vehicle holding the plate and the driver's own retry is refused
    /// <c>registration-exists</c> (D-37).
    /// </summary>
    [Fact]
    public async Task An_unresolvable_upload_id_on_the_registration_leaves_no_vehicle_behind()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var plate = RegistryHarness.NextPlate();

        var body = new
        {
            registrationNumber = plate,
            vehicleType = "three_wheeler",
            mode = "C",
            driverName = "Nimal Perera",
            insuranceFileId = Guid.NewGuid().ToString(),
        };

        await ProblemDocument.AssertAsync(
            await harness.PostAsync("/v1/vehicles", body, bearer), HttpStatusCode.BadRequest, "validation-failed");

        // The plate is still free, which is the whole point.
        var retried = await harness.PostAsync(
            "/v1/vehicles",
            new { registrationNumber = plate, vehicleType = "three_wheeler", mode = "C", driverName = "Nimal Perera" },
            bearer);

        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
    }

    /// <summary>
    /// A Fleet Portal vehicle has no wizard steps to derive from, so the AL-30 rule must not run
    /// over it and mark an approved bus Incomplete.
    /// </summary>
    [Fact]
    public async Task Recomputing_a_fleet_vehicle_is_refused_rather_than_marking_it_incomplete()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var ownerId = await harness.CreateDriverAsync();
        var vehicleId = await harness.SeedFleetVehicleAsync(ownerId);

        var response = await harness.PostInternalAsync(
            $"/v1/internal/vehicles/{vehicleId}/onboarding/recompute", null);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "mode-not-allowed");

        await using var connection = await harness.OpenAsync();
        Assert.Equal(
            "approved",
            await Dapper.SqlMapper.QuerySingleAsync<string>(
                connection,
                "SELECT onboarding_status FROM registry.vehicles WHERE id = @Id;",
                new { Id = vehicleId }));
    }

    /// <summary>D3' POST /v1/vehicles: "Side Effects: emits `vehicle.registered`".</summary>
    [Fact]
    public async Task Registering_emits_vehicle_registered_through_the_outbox()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var bearer = harness.Tokens.Driver(await harness.CreateDriverAsync());
        var registered = await harness.RegisterVehicleAsync(bearer);
        var vehicleId = Guid.Parse(registered.GetProperty("vehicleId").GetString()!);

        var queued = Assert.Single(await harness.OutboxAsync(vehicleId), e => e.EventType == "vehicle.registered");

        Assert.Equal(vehicleId, queued.AggregateId);

        using var payload = JsonDocument.Parse(queued.Payload);
        Assert.Equal(
            registered.GetProperty("registrationNumber").GetString(),
            payload.RootElement.GetProperty("registrationNumber").GetString());
    }
}
