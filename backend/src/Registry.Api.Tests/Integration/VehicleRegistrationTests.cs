using System.Net;
using Dapper;
using MageRide.Registry.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Registry.Tests.Integration;

/// <summary>
/// <c>POST /v1/vehicles</c> — AL-09's type set and D-37's plate uniqueness, against the real
/// constraints.
/// </summary>
[Collection<PostgresCollection>]
public sealed class VehicleRegistrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_driver_registers_a_mode_c_three_wheeler_and_it_starts_pending()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var plate = RegistryHarness.NextPlate();

        var body = await harness.RegisterVehicleAsync(harness.Tokens.Driver(driverId), plate);

        Assert.Equal(plate, body.GetProperty("registrationNumber").GetString());
        Assert.Equal("three_wheeler", body.GetProperty("vehicleType").GetString());
        Assert.Equal("C", body.GetProperty("mode").GetString());

        // A brand-new registration is PENDING and Incomplete: nothing has been verified, and
        // AL-30 only reaches "approved" once all four steps are VERIFIED (C029).
        Assert.Equal("PENDING", body.GetProperty("status").GetString());
        Assert.Equal("incomplete", body.GetProperty("onboardingStatus").GetString());

        var verification = body.GetProperty("verification");
        foreach (var step in new[] { "vehicleDetails", "insurance", "revenueLicense", "photos" })
        {
            Assert.Equal("PENDING_INPUT", verification.GetProperty(step).GetString());
        }
    }

    [Fact]
    public async Task Registering_creates_the_driver_profile_the_vehicle_row_needs()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        await harness.RegisterVehicleAsync(harness.Tokens.Driver(driverId), driverName: "Nimal Perera");

        await using var connection = await harness.OpenAsync();

        // registry.vehicles.driver_name is NOT NULL and is what a passenger sees (US-2.12).
        // Profile Setup, which is where the name normally comes from, is C029's.
        Assert.Equal(
            "Nimal Perera",
            await connection.QuerySingleAsync<string>(
                "SELECT display_name FROM registry.driver_profiles WHERE driver_id = @DriverId;",
                new { DriverId = driverId }));
        Assert.Equal(
            "Nimal Perera",
            await connection.QuerySingleAsync<string>(
                "SELECT driver_name FROM registry.vehicles WHERE owner_id = @DriverId;",
                new { DriverId = driverId }));
    }

    [Fact]
    public async Task A_second_vehicle_reuses_the_profile_name_without_being_told_it_again()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var bearer = harness.Tokens.Driver(await harness.CreateDriverAsync());
        await harness.RegisterVehicleAsync(bearer, driverName: "Nimal Perera");

        // driverName is "defaults from registry.driver_profiles" in the contract; once the
        // profile exists the second registration must not need it (US-2.8, multi-vehicle).
        var second = await harness.PostAsync(
            "/v1/vehicles",
            new { registrationNumber = RegistryHarness.NextPlate(), vehicleType = "sedan", mode = "C" },
            bearer);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        await using var connection = await harness.OpenAsync();
        Assert.Equal(
            "Nimal Perera",
            await connection.QuerySingleAsync<string>(
                "SELECT driver_name FROM registry.vehicles WHERE id = @Id;",
                new { Id = Guid.Parse((await RegistryHarness.ReadJsonAsync(second)).GetProperty("vehicleId").GetString()!) }));
    }

    [Fact]
    public async Task A_first_vehicle_with_no_name_anywhere_is_refused_rather_than_written_blank()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var response = await harness.PostAsync(
            "/v1/vehicles",
            new { registrationNumber = RegistryHarness.NextPlate(), vehicleType = "sedan", mode = "C" },
            harness.Tokens.Driver(await harness.CreateDriverAsync()));

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    [Theory]
    [InlineData("car")]
    [InlineData("Car")]
    [InlineData("tuk")]
    [InlineData("sedan_xl")]
    public async Task A_non_canonical_vehicle_type_is_rejected(string vehicleType)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var response = await harness.PostAsync(
            "/v1/vehicles",
            new { registrationNumber = RegistryHarness.NextPlate(), vehicleType, mode = "C", driverName = "Test" },
            harness.Tokens.Driver(await harness.CreateDriverAsync()));

        var problem = await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "invalid-vehicle-type");

        // The detail names the AL-09 replacement, so a client sending the old value learns what
        // to send instead rather than only that it was wrong.
        Assert.Contains("sedan", problem.Root.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bus")]
    [InlineData("train")]
    public async Task A_mode_a_vehicle_type_is_refused_on_the_driver_app(string vehicleType)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var response = await harness.PostAsync(
            "/v1/vehicles",
            new { registrationNumber = RegistryHarness.NextPlate(), vehicleType, mode = "C", driverName = "Test" },
            harness.Tokens.Driver(await harness.CreateDriverAsync()));

        // A real AL-09 type, just not one this surface onboards — buses go to the Fleet Portal
        // and trains are admin-only, so it is 403, not 400.
        await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "mode-not-allowed");
    }

    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    public async Task Mode_a_and_mode_b_are_refused_on_the_driver_app(string mode)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var response = await harness.PostAsync(
            "/v1/vehicles",
            new { registrationNumber = RegistryHarness.NextPlate(), vehicleType = "van", mode, driverName = "Test" },
            harness.Tokens.Driver(await harness.CreateDriverAsync()));

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "mode-not-allowed");
    }

    [Fact]
    public async Task A_duplicate_registration_in_the_active_set_is_rejected()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var plate = RegistryHarness.NextPlate();
        await harness.RegisterVehicleAsync(harness.Tokens.Driver(await harness.CreateDriverAsync()), plate);

        // D-37 is a platform-wide rule, not a per-driver one: a second driver cannot claim the
        // plate either, which is the case that matters — one plate, one live registration.
        var response = await harness.PostAsync(
            "/v1/vehicles",
            new { registrationNumber = plate, vehicleType = "sedan", mode = "C", driverName = "Other" },
            harness.Tokens.Driver(await harness.CreateDriverAsync()));

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Conflict, "registration-exists");
    }

    [Fact]
    public async Task The_same_plate_typed_differently_is_still_a_duplicate()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var bearer = harness.Tokens.Driver(await harness.CreateDriverAsync());
        var plate = RegistryHarness.NextPlate();
        await harness.RegisterVehicleAsync(bearer, plate);

        // Without canonicalisation this is two rows and one plate, and D-37 stops meaning
        // anything — the index is over the stored text.
        var response = await harness.PostAsync(
            "/v1/vehicles",
            new { registrationNumber = plate.Replace('-', ' ').ToLowerInvariant(), vehicleType = "sedan", mode = "C" },
            bearer);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Conflict, "registration-exists");
    }

    [Fact]
    public async Task A_rejected_registration_frees_its_plate()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var plate = RegistryHarness.NextPlate();
        var body = await harness.RegisterVehicleAsync(harness.Tokens.Driver(await harness.CreateDriverAsync()), plate);

        // Rejection is C029's endpoint; the rule under test is D-37's partial index, which is
        // C021's to keep working. Driven through the database rather than an endpoint that does
        // not exist yet.
        await using (var connection = await harness.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE registry.vehicles SET status = 'REJECTED' WHERE id = @Id;",
                new { Id = Guid.Parse(body.GetProperty("vehicleId").GetString()!) });
        }

        var response = await harness.PostAsync(
            "/v1/vehicles",
            new { registrationNumber = plate, vehicleType = "sedan", mode = "C", driverName = "Other" },
            harness.Tokens.Driver(await harness.CreateDriverAsync()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task An_unregistered_driver_cannot_create_a_vehicle_for_someone_else()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        // owner_id comes from the token's sub, never from the body, so there is no field to
        // supply somebody else's id in. This asserts a body that tries anyway is ignored.
        var driverId = await harness.CreateDriverAsync();
        var otherId = await harness.CreateDriverAsync();

        var response = await harness.PostAsync(
            "/v1/vehicles",
            new
            {
                registrationNumber = RegistryHarness.NextPlate(),
                vehicleType = "sedan",
                mode = "C",
                driverName = "Test",
                ownerId = otherId,
            },
            harness.Tokens.Driver(driverId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var connection = await harness.OpenAsync();
        Assert.Equal(
            driverId,
            await connection.QuerySingleAsync<Guid>(
                "SELECT owner_id FROM registry.vehicles WHERE id = @Id;",
                new { Id = Guid.Parse((await RegistryHarness.ReadJsonAsync(response)).GetProperty("vehicleId").GetString()!) }));
    }
}
