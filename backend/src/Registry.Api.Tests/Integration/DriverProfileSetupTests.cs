using System.Net;
using System.Text.Json;
using Dapper;
using MageRide.Registry.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Registry.Tests.Integration;

/// <summary>
/// <c>PUT /v1/drivers/profile</c> — AL-27's phase 1, and AL-29's per-field provenance — and
/// <c>GET /v1/drivers/profile</c>, the read that tells the boot router which phase this is
/// (Δ MCS-05).
/// </summary>
[Collection<PostgresCollection>]
public sealed class DriverProfileSetupTests(PostgresFixture postgres)
{
    /// <summary>
    /// Δ MCS-05. The cold-start read, before Profile Setup has been done.
    /// </summary>
    /// <remarks>
    /// <b>A literal <c>null</c> and a 200, not a 404.</b> "This driver has no profile yet" is the
    /// normal answer on a boot — it is what sends them to Profile Setup — and a 404 is something
    /// the app puts in front of them as an error over the top of the right behaviour. ride-svc's
    /// two recovery reads are shaped the same way for the same reason.
    /// </remarks>
    [Fact]
    public async Task A_driver_who_has_not_done_profile_setup_reads_null_rather_than_a_404()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();

        var response = await harness.GetAsync("/v1/drivers/profile", harness.Tokens.Driver(driverId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("null", (await response.Content.ReadAsStringAsync()).Trim());
    }

    /// <summary>
    /// Δ MCS-05. After Profile Setup, the read answers what was stored.
    /// </summary>
    /// <remarks>
    /// This is the question SCR-DA/DI-001 actually asks, and until this operation existed both
    /// apps asked iam-svc instead — <c>GET /v1/users/me</c>, is there a <c>first_name</c>? Profile
    /// Setup writes <c>registry.driver_profiles</c> and never touches <c>iam.users</c>, so that
    /// read was wrong in both directions: a driver who had done this went round the form again on
    /// every cold start, and a passenger who had a name from the other app skipped it entirely.
    /// </remarks>
    [Fact]
    public async Task The_read_answers_what_profile_setup_stored()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);
        await harness.CompleteProfileSetupAsync(driverId, bearer);

        var response = await harness.GetAsync("/v1/drivers/profile", bearer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await RegistryHarness.ReadJsonAsync(response);
        Assert.Equal(driverId.ToString(), body.GetProperty("driverId").GetString());
        Assert.Equal("Nimal Perera", body.GetProperty("displayName").GetString());

        // The same derivation the write returns, off the same `verified_at` column — the two must
        // not be able to disagree about a verdict the driver has already been shown.
        Assert.Equal("APPROVED", body.GetProperty("status").GetString());

        // The row, and deliberately not the AL-29 fields: those belong to the licence documents
        // and come back from the write, on the screen that can act on them.
        Assert.False(body.TryGetProperty("fields", out _));
    }

    [Fact]
    public async Task Profile_setup_stores_the_profile_and_a_vehicle_less_driving_licence()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();

        var body = await harness.CompleteProfileSetupAsync(driverId, harness.Tokens.Driver(driverId));

        Assert.Equal("Nimal Perera", body.GetProperty("displayName").GetString());
        Assert.Equal("APPROVED", body.GetProperty("status").GetString());

        // AL-29: the NIC and the licence classes come off the scan, not off the request.
        Assert.Equal("199012345678", body.GetProperty("nicNo").GetString());
        Assert.Equal(
            new[] { "three_wheeler", "sedan" },
            body.GetProperty("allowedVehicleTypes").EnumerateArray().Select(item => item.GetString()));

        await using var connection = await harness.OpenAsync();

        // AL-27: the driving licence is captured at Profile Setup, which precedes any vehicle, so
        // the rows are vehicle-less. Front and back are separate documents because the Sri Lankan
        // licence carries its classes on the reverse.
        Assert.Equal(
            2,
            await connection.ExecuteScalarAsync<int>(
                """
                SELECT count(*) FROM registry.documents
                 WHERE driver_id = @DriverId AND kind = 'driving_license' AND vehicle_id IS NULL;
                """,
                new { DriverId = driverId }));

        Assert.NotNull(await connection.QuerySingleAsync<DateTimeOffset?>(
            "SELECT verified_at FROM registry.driver_profiles WHERE driver_id = @DriverId;",
            new { DriverId = driverId }));
    }

    /// <summary>
    /// DoD item 1 on the identity half: a driver-typed value is never silently trusted (BR-25.2).
    /// </summary>
    [Fact]
    public async Task A_driver_typed_field_is_manual_pending_and_reaches_the_officer_queue()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();

        var body = await harness.CompleteProfileSetupAsync(
            driverId, harness.Tokens.Driver(driverId), overrides: new { nicNo = "200156789012" });

        // The driver corrected an unclear scan, so the profile carries their value and the whole
        // identity stays PENDING until an officer confirms it (US-2.4a).
        Assert.Equal("200156789012", body.GetProperty("nicNo").GetString());
        Assert.Equal("PENDING", body.GetProperty("status").GetString());

        var nic = Assert.Single(
            body.GetProperty("fields").EnumerateArray().ToArray(),
            field => field.GetProperty("key").GetString() == "nic_no");

        Assert.Equal("manual", nic.GetProperty("source").GetString());
        Assert.Equal("pending", nic.GetProperty("verifyStatus").GetString());

        // ck_document_fields_manual_confidence: a hand-typed value carries no confidence, because
        // a number invented for something nobody scanned would read as evidence.
        Assert.False(nic.TryGetProperty("confidence", out _));

        await using var connection = await harness.OpenAsync();

        Assert.Null(await connection.QuerySingleAsync<DateTimeOffset?>(
            "SELECT verified_at FROM registry.driver_profiles WHERE driver_id = @DriverId;",
            new { DriverId = driverId }));

        // US-2.4a routes it to SCR-AP-003. The event is keyed by the driver: an identity has no
        // vehicle to partition by.
        var queued = Assert.Single(await harness.OutboxAsync(driverId));
        Assert.Equal("document.review_required", queued.EventType);

        using var payload = JsonDocument.Parse(queued.Payload);
        Assert.Equal("profile", payload.RootElement.GetProperty("step").GetString());
        Assert.Contains(
            "nic_no",
            payload.RootElement.GetProperty("pendingFieldKeys").EnumerateArray().Select(key => key.GetString()));
    }

    /// <summary>AL-27 makes the photo required, not optional.</summary>
    [Fact]
    public async Task The_profile_photo_is_required()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();

        var response = await harness.PutAsync(
            "/v1/drivers/profile",
            new
            {
                driverName = "Nimal Perera",
                licenseFrontFileId = await harness.SeedUploadAsync(driverId),
                licenseBackFileId = await harness.SeedUploadAsync(driverId),
            },
            harness.Tokens.Driver(driverId));

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    [Fact]
    public async Task An_upload_belonging_to_another_driver_cannot_be_attached()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var strangerId = await harness.CreateDriverAsync();

        // Without the ownership check a driver could attach somebody else's licence scan and have
        // its extracted number verify against their own profile.
        var response = await harness.PutAsync(
            "/v1/drivers/profile",
            new
            {
                driverName = "Nimal Perera",
                profilePhotoFileId = await harness.SeedUploadAsync(driverId),
                licenseFrontFileId = await harness.SeedUploadAsync(strangerId),
                licenseBackFileId = await harness.SeedUploadAsync(driverId),
            },
            harness.Tokens.Driver(driverId));

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    /// <summary>
    /// The AL-27 fence, stated as a test: Profile Setup precedes Home and needs no vehicle, and
    /// the name it stores is the one a later registration inherits.
    /// </summary>
    [Fact]
    public async Task Profile_setup_needs_no_vehicle_and_names_the_one_registered_afterwards()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        await harness.CompleteProfileSetupAsync(driverId, bearer, "Kamala Silva");

        // No driverName on the body: C021 required one because Profile Setup did not exist yet.
        var registered = await harness.PostAsync(
            "/v1/vehicles",
            new { registrationNumber = RegistryHarness.NextPlate(), vehicleType = "three_wheeler", mode = "C" },
            bearer);

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        await using var connection = await harness.OpenAsync();

        var vehicle = await connection.QuerySingleAsync<(string DriverName, string? DriverPhotoUrl)>(
            "SELECT driver_name, driver_photo_url FROM registry.vehicles WHERE owner_id = @DriverId;",
            new { DriverId = driverId });

        Assert.Equal("Kamala Silva", vehicle.DriverName);
        Assert.NotNull(vehicle.DriverPhotoUrl);
    }

    [Fact]
    public async Task A_second_profile_setup_overwrites_the_name_and_photo()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        var first = await harness.CompleteProfileSetupAsync(driverId, bearer, "Nimal Perera");
        var second = await harness.CompleteProfileSetupAsync(driverId, bearer, "Nimal J Perera");

        // Profile Setup owns the name and the photo, so it overwrites them; a vehicle registration
        // only ever fills them in when they are absent.
        Assert.Equal("Nimal J Perera", second.GetProperty("displayName").GetString());
        Assert.NotEqual(
            first.GetProperty("photoUrl").GetString(), second.GetProperty("photoUrl").GetString());
    }

    /// <summary>
    /// Δ MCS-11 — a licence class is not a vehicle type, and this route must not answer one as the
    /// other.
    /// </summary>
    /// <remarks>
    /// `registry.yaml` types `allowedVehicleTypes` as `VehicleType[]` on BOTH the request and the
    /// 200. <c>RequireAllowedVehicleTypes</c> has always enforced that on a driver-typed value; the
    /// EXTRACTED value went to the column through <c>SplitTypes</c> with no check at all. So once
    /// ocr-svc actually started reading licences (MCS-07), Profile Setup answered 200 with
    /// <c>["B","G1"]</c> in an enum-typed field and every strict client rejected the whole body —
    /// which reached the driver as "Something went wrong. Please try again." on SCR-DA-003a.
    /// </remarks>
    [Fact]
    public async Task Extracted_licence_classes_never_reach_the_vehicle_type_field()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        harness.Ocr.ReadsLicenceClasses("B,G1");

        var driverId = await harness.CreateDriverAsync();
        var body = await harness.CompleteProfileSetupAsync(driverId, harness.Tokens.Driver(driverId));

        // The contract's field carries only AL-09 types. "B" and "G1" are neither, so it is empty
        // rather than wrong — this is the assertion the driver app's decode makes for real.
        Assert.Empty(body.GetProperty("allowedVehicleTypes").EnumerateArray());

        // The classes are NOT lost. The raw reading stays on the extract card and in the officer
        // queue, which is where the evidence belongs and where a future mapping would come from.
        var extracted = body.GetProperty("fields").EnumerateArray()
            .Single(field => field.GetProperty("key").GetString() == "allowed_vehicle_types");

        Assert.Equal("B,G1", extracted.GetProperty("value").GetString());

        // And it is PENDING however sure ocr-svc was: a confident reading of a vocabulary this
        // platform cannot act on is an officer's decision, exactly like a confident `reg_no_match`
        // of the wrong plate.
        Assert.Equal("pending", extracted.GetProperty("verifyStatus").GetString());
        Assert.Equal("PENDING", body.GetProperty("status").GetString());
    }

    /// <summary>A licence whose classes DO happen to be canonical still passes them through.</summary>
    [Fact]
    public async Task Canonical_extracted_types_are_still_promoted_to_the_profile()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var body = await harness.CompleteProfileSetupAsync(driverId, harness.Tokens.Driver(driverId));

        Assert.Equal(
            new[] { "three_wheeler", "sedan" },
            body.GetProperty("allowedVehicleTypes").EnumerateArray().Select(item => item.GetString()));
    }
}
