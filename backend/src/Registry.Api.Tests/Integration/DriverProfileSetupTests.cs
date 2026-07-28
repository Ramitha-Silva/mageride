using System.Net;
using System.Text.Json;
using Dapper;
using MageRide.Registry.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Registry.Tests.Integration;

/// <summary>
/// <c>PUT /v1/drivers/profile</c> — AL-27's phase 1, and AL-29's per-field provenance.
/// </summary>
[Collection<PostgresCollection>]
public sealed class DriverProfileSetupTests(PostgresFixture postgres)
{
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
}
