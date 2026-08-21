using System.Net;
using MageRide.Registry.Tests.Infrastructure;
using MageRide.Shared.Auth;
using MageRide.TestKit;

namespace MageRide.Registry.Tests.Integration;

/// <summary>
/// The rest of the vehicle lifecycle: read, status, deactivate (US-2.16), the passenger-facing
/// driver profile (US-2.12), and the fence that keeps trains out of the Driver App.
/// </summary>
[Collection<RegistryCollection>]
public sealed class VehicleLifecycleTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_vehicle_reads_back_with_its_entitlement_and_eligibility()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        var vehicleId = await harness.RegisterApprovedVehicleAsync(bearer);

        var body = await RegistryHarness.ReadJsonAsync(await harness.GetAsync($"/v1/vehicles/{vehicleId}", bearer));

        Assert.Equal(vehicleId, body.GetProperty("vehicleId").GetString());
        Assert.Equal("APPROVED", body.GetProperty("status").GetString());
        Assert.Equal("owned", body.GetProperty("source").GetString());
        Assert.True(body.GetProperty("isGoLiveEligible").GetBoolean());
        Assert.False(body.GetProperty("isSelected").GetBoolean());
    }

    [Fact]
    public async Task The_status_poll_answers_while_a_registration_is_pending()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        var vehicle = await harness.RegisterVehicleAsync(bearer);
        var vehicleId = vehicle.GetProperty("vehicleId").GetString();

        var body = await RegistryHarness.ReadJsonAsync(
            await harness.GetAsync($"/v1/vehicles/{vehicleId}/status", bearer));

        Assert.Equal("PENDING", body.GetProperty("status").GetString());
        Assert.False(body.TryGetProperty("rejectionReason", out _));
    }

    [Fact]
    public async Task Another_drivers_vehicle_is_not_readable()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var mineId = await harness.CreateDriverAsync();
        var theirsId = await harness.CreateDriverAsync();

        var vehicleId = await harness.RegisterApprovedVehicleAsync(harness.Tokens.Driver(theirsId));

        await ProblemDocument.AssertAsync(
            await harness.GetAsync($"/v1/vehicles/{vehicleId}", harness.Tokens.Driver(mineId)),
            HttpStatusCode.NotFound,
            "vehicle-not-found");
    }

    /// <summary>
    /// US-2.16 and D-37 together: DEACTIVATED is outside <c>ux_vehicles_regno_active</c>'s
    /// predicate, so retiring a vehicle frees its plate for a fresh registration.
    /// </summary>
    [Fact]
    public async Task Deactivating_releases_the_plate_for_a_new_registration()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var plate = RegistryHarness.NextPlate();

        var first = await harness.RegisterVehicleAsync(bearer, plate);
        var firstId = first.GetProperty("vehicleId").GetString();

        // The plate is still taken while the first registration lives.
        var blocked = await harness.PostAsync(
            "/v1/vehicles",
            new { registrationNumber = plate, vehicleType = "three_wheeler", mode = "C", driverName = "Test Driver" },
            bearer);
        await ProblemDocument.AssertAsync(blocked, HttpStatusCode.Conflict, "registration-exists");

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await harness.PostAsync($"/v1/vehicles/{firstId}/deactivate", null, bearer)).StatusCode);

        var second = await harness.RegisterVehicleAsync(bearer, plate);
        Assert.NotEqual(firstId, second.GetProperty("vehicleId").GetString());
    }

    [Fact]
    public async Task Deactivating_twice_is_a_conflict()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        var vehicleId = await harness.RegisterApprovedVehicleAsync(bearer);

        await harness.PostAsync($"/v1/vehicles/{vehicleId}/deactivate", null, bearer);

        await ProblemDocument.AssertAsync(
            await harness.PostAsync($"/v1/vehicles/{vehicleId}/deactivate", null, bearer),
            HttpStatusCode.Conflict,
            "conflict");
    }

    /// <summary>
    /// The C021 handoff left this to C028: the foreign key fires on DELETE and a status change is
    /// not one, so a deactivated vehicle would otherwise stay selected and fail every go-online.
    /// </summary>
    [Fact]
    public async Task Deactivating_the_selected_vehicle_clears_the_selection()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        var vehicleId = await harness.RegisterApprovedVehicleAsync(bearer);
        await harness.PostAsync($"/v1/vehicles/{vehicleId}/select-live", null, bearer);
        Assert.Equal(1, await harness.ActiveSelectionCountAsync(driverId));

        await harness.PostAsync($"/v1/vehicles/{vehicleId}/deactivate", null, bearer);

        Assert.Equal(0, await harness.ActiveSelectionCountAsync(driverId));
    }

    [Fact]
    public async Task Deactivating_a_vehicle_the_caller_does_not_own_is_refused()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var mineId = await harness.CreateDriverAsync();
        var theirsId = await harness.CreateDriverAsync();

        var vehicleId = await harness.RegisterApprovedVehicleAsync(harness.Tokens.Driver(theirsId));

        await ProblemDocument.AssertAsync(
            await harness.PostAsync($"/v1/vehicles/{vehicleId}/deactivate", null, harness.Tokens.Driver(mineId)),
            HttpStatusCode.Forbidden,
            "not-owner");
    }

    /// <summary>
    /// US-13.7 puts retiring a fleet vehicle on the fleet operator, in the Fleet Portal. An
    /// assigned driver may operate it and may not take it off the map.
    /// </summary>
    [Fact]
    public async Task An_assigned_driver_may_not_deactivate_the_fleets_vehicle()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var fleetOwnerId = await harness.CreateDriverAsync();
        var driverId = await harness.CreateDriverAsync();

        var vehicleId = await harness.SeedFleetVehicleAsync(fleetOwnerId);
        await harness.AssignToFleetAsync(vehicleId, driverId, fleetOwnerId);

        await ProblemDocument.AssertAsync(
            await harness.PostAsync($"/v1/vehicles/{vehicleId}/deactivate", null, harness.Tokens.Driver(driverId)),
            HttpStatusCode.Forbidden,
            "not-owner");
    }

    [Fact]
    public async Task The_passenger_facing_driver_profile_can_be_edited()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        var vehicleId = await harness.RegisterApprovedVehicleAsync(bearer);

        var updated = await harness.PutAsync(
            $"/v1/vehicles/{vehicleId}/driver-profile",
            new { name = "Nimal Perera", photoUrl = "https://cdn.mageride.lk/d/1.jpg" },
            bearer);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var body = await RegistryHarness.ReadJsonAsync(updated);
        Assert.Equal("Nimal Perera", body.GetProperty("driverName").GetString());
        Assert.Equal("https://cdn.mageride.lk/d/1.jpg", body.GetProperty("driverPhotoUrl").GetString());

        // An absent field leaves the column alone; an empty photoUrl clears it.
        var renamed = await harness.PutAsync(
            $"/v1/vehicles/{vehicleId}/driver-profile", new { name = "Nimal" }, bearer);
        Assert.Equal(
            "https://cdn.mageride.lk/d/1.jpg",
            (await RegistryHarness.ReadJsonAsync(renamed)).GetProperty("driverPhotoUrl").GetString());

        var cleared = await harness.PutAsync(
            $"/v1/vehicles/{vehicleId}/driver-profile", new { photoUrl = "" }, bearer);
        Assert.False((await RegistryHarness.ReadJsonAsync(cleared)).TryGetProperty("driverPhotoUrl", out _));
    }

    [Theory]
    [InlineData("not-a-url")]
    public async Task A_driver_photo_that_is_not_a_url_is_refused(string photoUrl)
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        var vehicleId = await harness.RegisterApprovedVehicleAsync(bearer);

        await ProblemDocument.AssertAsync(
            await harness.PutAsync($"/v1/vehicles/{vehicleId}/driver-profile", new { photoUrl }, bearer),
            HttpStatusCode.BadRequest,
            "validation-failed");
    }

    /// <summary>
    /// DoD item 4, and the C028 fence: "Train (Mode A) registration is admin-only via admin-bff.
    /// The Driver App must expose no train path."
    /// </summary>
    [Theory]
    [InlineData("train")]
    [InlineData("bus")]
    public async Task No_endpoint_creates_a_Mode_A_vehicle_from_a_driver_token(string vehicleType)
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        // The type is real — it is in the AL-09 enumeration and in the column's CHECK — so this is
        // 403 mode-not-allowed rather than 400: the Driver App is the wrong surface, not the value.
        await ProblemDocument.AssertAsync(
            await harness.PostAsync(
                "/v1/vehicles",
                new { registrationNumber = RegistryHarness.NextPlate(), vehicleType, mode = "C", driverName = "Test" },
                bearer),
            HttpStatusCode.Forbidden,
            "mode-not-allowed");

        // And the mode itself is refused whatever the type says.
        await ProblemDocument.AssertAsync(
            await harness.PostAsync(
                "/v1/vehicles",
                new { registrationNumber = RegistryHarness.NextPlate(), vehicleType = "van", mode = "A", driverName = "Test" },
                bearer),
            HttpStatusCode.Forbidden,
            "mode-not-allowed");
    }

    /// <summary>
    /// The whole vehicle surface is driver-only: a token without the <c>driver</c> role is refused,
    /// which is deny-by-default working as intended.
    /// </summary>
    /// <remarks>
    /// The token here is <b>synthetic</b>. Signing into the Driver App now grants the driver role
    /// additively (iam-svc, at OTP verify), so <c>app=driver</c> with only <c>role=passenger</c> is
    /// no longer a shape a real sign-in produces. It is still exactly the shape this gate has to
    /// refuse — an internal caller, a stale token minted before the grant, or a bug upstream — so
    /// the assertion stands and only the story behind it changed.
    /// </remarks>
    [Fact]
    public async Task The_new_routes_require_the_driver_role()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var ownerId = await harness.CreateDriverAsync();
        var passengerId = await harness.CreateDriverAsync();

        var vehicleId = await harness.RegisterApprovedVehicleAsync(harness.Tokens.Driver(ownerId));
        var passenger = harness.Tokens.PassengerOnDriverApp(passengerId);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await harness.GetAsync($"/v1/vehicles/{vehicleId}", passenger)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await harness.PostAsync($"/v1/vehicles/{vehicleId}/deactivate", null, passenger)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await harness.PostAsync($"/v1/vehicles/{vehicleId}/share", new { userId = ownerId.ToString() }, passenger))
                .StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await harness.GetAsync($"/v1/vehicles/{vehicleId}/subscribers", passenger)).StatusCode);
    }

    /// <summary>
    /// The three counterparty routes deliberately do <b>not</b> demand the driver role: a
    /// passenger accepts nothing, but they do unsubscribe (US-NEW.1) and ask for access (US-4.5).
    /// </summary>
    [Fact]
    public async Task A_passenger_may_ask_for_access_to_a_Mode_B_vehicle()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var fleetOwnerId = await harness.CreateDriverAsync();
        var passengerId = await harness.CreateDriverAsync();

        var vehicleId = await harness.SeedFleetVehicleAsync(fleetOwnerId);

        var response = await harness.PostAsync(
            "/v1/share-requests",
            new { vehicleId = vehicleId.ToString() },
            harness.Tokens.Issue(passengerId, [MageRideRoles.Passenger], MageRideApps.Passenger));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await RegistryHarness.ReadJsonAsync(response);
        Assert.Equal("pending", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Asking_twice_returns_the_same_open_request()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var fleetOwnerId = await harness.CreateDriverAsync();
        var passengerId = await harness.CreateDriverAsync();
        var passenger = harness.Tokens.Issue(passengerId, [MageRideRoles.Passenger], MageRideApps.Passenger);

        var vehicleId = await harness.SeedFleetVehicleAsync(fleetOwnerId);

        var first = await harness.PostAsync("/v1/share-requests", new { vehicleId = vehicleId.ToString() }, passenger);
        var second = await harness.PostAsync("/v1/share-requests", new { vehicleId = vehicleId.ToString() }, passenger);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(
            (await RegistryHarness.ReadJsonAsync(first)).GetProperty("requestId").GetString(),
            (await RegistryHarness.ReadJsonAsync(second)).GetProperty("requestId").GetString());
    }

    /// <summary>Mode B is the only mode with private tracking access (AL-23).</summary>
    [Fact]
    public async Task Access_cannot_be_requested_for_a_Mode_C_vehicle()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var ownerId = await harness.CreateDriverAsync();
        var passengerId = await harness.CreateDriverAsync();

        var vehicleId = await harness.RegisterApprovedVehicleAsync(harness.Tokens.Driver(ownerId));

        await ProblemDocument.AssertAsync(
            await harness.PostAsync(
                "/v1/share-requests",
                new { vehicleId },
                harness.Tokens.Issue(passengerId, [MageRideRoles.Passenger], MageRideApps.Passenger)),
            HttpStatusCode.Forbidden,
            "mode-not-allowed");
    }
}
