using System.Text.Json;
using MageRide.PublicBff.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.PublicBff.Tests.Integration;

/// <summary>
/// "Each scope returns exactly the documented field set and nothing more."
/// </summary>
/// <remarks>
/// <b>Asserted on the JSON, not on the DTO.</b> A test that deserialised into
/// <c>PackageSnapshotResponse</c> would prove that the fields it knows about are right and would say
/// nothing about the ones it does not — which is the half that matters, because P-02/P-09 are claims
/// about what is <em>absent</em>. So every property name that comes back is compared against the
/// contract's list.
/// </remarks>
[Collection<PublicBffCollection>]
public sealed class SnapshotTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>`public-bff.yaml#PackageSnapshot`.</summary>
    private static readonly string[] PackageFields =
        ["kind", "status", "driver", "position", "deliveryOtp", "dropoff", "senderNameMasked"];

    /// <summary>`public-bff.yaml#ProxyRideSnapshot`.</summary>
    private static readonly string[] ProxyFields =
        ["kind", "state", "driver", "position", "etaMin", "startOtp", "route", "fare"];

    /// <summary>`public-bff.yaml#PickupConfirmSnapshot`.</summary>
    private static readonly string[] PickupFields =
        ["kind", "bookerFirstName", "suggestedPin", "expiresAt", "ttlRemainingSec"];

    /// <summary>`public-bff.yaml#PublicDriver`.</summary>
    private static readonly string[] DriverFields = ["name", "photo", "vehicleType", "regNo", "phone"];

    [Fact]
    public async Task A_package_recipient_sees_the_parcel_the_driver_and_their_code_and_nothing_else()
    {
        await using var harness = await StartAsync();

        var ride = await harness.Seed.RideAsync(state: "InProgress", kind: 2);
        var token = await harness.Seed.TokenAsync(
            ride.RideId, "package_recipient", harness.Now.AddHours(4));

        await harness.Seed.PositionAsync(
            ride.VehicleId, PublicBffSeed.DropoffLat, PublicBffSeed.DropoffLng, harness.Now);

        await harness.Seed.DeliveryCodeAsync(ride.RideId, "4821");

        var body = await PublicBffHarness.OkAsync(
            await harness.GetAsync($"/public/track/{token}"), "the package snapshot");

        AssertOnlyFields(body, PackageFields);
        Assert.Equal("package", body.GetProperty("kind").GetString());

        // Aboard and away from the sender — the fourth step SCR-WT-002 draws, derived from the one
        // fact that separates it from PickedUp.
        Assert.Equal("InTransit", body.GetProperty("status").GetString());

        // US-20.5's code, which exists nowhere in `rides.rides` — only in the Redis key
        // notification-svc leaves it in when it mints this very token.
        Assert.Equal("4821", body.GetProperty("deliveryOtp").GetString());

        var driver = body.GetProperty("driver");
        AssertOnlyFields(driver, DriverFields);
        Assert.Equal("Kasun Perera", driver.GetProperty("name").GetString());

        // AL-48: the real number, for a plain tel: link. Not a lease, not a mask.
        Assert.Equal(ride.DriverPhone, driver.GetProperty("phone").GetString());

        // P-09: the sender's display name and never their number. There is no field on the type
        // that could carry one, which is the fence — this asserts the consequence.
        Assert.Equal("Sanduni", body.GetProperty("senderNameMasked").GetString());
        Assert.DoesNotContain(ride.BookerPhone, body.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_package_that_has_not_left_the_sender_is_PickedUp_and_one_still_waiting_is_PickupPending()
    {
        await using var harness = await StartAsync();

        var waiting = await harness.Seed.RideAsync(state: "DriverArrived", kind: 2);
        var waitingToken = await harness.Seed.TokenAsync(
            waiting.RideId, "package_recipient", harness.Now.AddHours(4));

        var aboard = await harness.Seed.RideAsync(state: "InProgress", kind: 2);
        var aboardToken = await harness.Seed.TokenAsync(
            aboard.RideId, "package_recipient", harness.Now.AddHours(4));

        // Still at the kerb outside the sender.
        await harness.Seed.PositionAsync(
            aboard.VehicleId, PublicBffSeed.PickupLat, PublicBffSeed.PickupLng, harness.Now);

        var waitingBody = await PublicBffHarness.OkAsync(
            await harness.GetAsync($"/public/track/{waitingToken}"), "the pickup-pending snapshot");

        var aboardBody = await PublicBffHarness.OkAsync(
            await harness.GetAsync($"/public/track/{aboardToken}"), "the picked-up snapshot");

        Assert.Equal("PickupPending", waitingBody.GetProperty("status").GetString());
        Assert.Equal("PickedUp", aboardBody.GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_delivery_code_is_shown_only_while_the_parcel_is_aboard()
    {
        await using var harness = await StartAsync();

        var ride = await harness.Seed.RideAsync(state: "DriverArrived", kind: 2);
        var token = await harness.Seed.TokenAsync(
            ride.RideId, "package_recipient", harness.Now.AddHours(4));

        // A stale code left over from a previous parcel on the same ride id would be the worst
        // case: the recipient reads out four digits the driver's app will refuse.
        await harness.Seed.DeliveryCodeAsync(ride.RideId, "4821");

        var body = await PublicBffHarness.OkAsync(
            await harness.GetAsync($"/public/track/{token}"), "a pre-pickup snapshot");

        // Absent, not null: the kernel's serializer omits a null member (D3' §0), so "the code is
        // not shown" and "there is no such field" are the same wire fact.
        Assert.False(body.TryGetProperty("deliveryOtp", out _));
    }

    [Fact]
    public async Task A_proxy_rider_sees_who_pays_and_never_how()
    {
        await using var harness = await StartAsync();

        var ride = await harness.Seed.RideAsync(
            state: "Accepted", kind: 1, paymentMethod: "cash", fareEstimateMinor: 45_000);

        var token = await harness.Seed.TokenAsync(
            ride.RideId, "proxy_rider", harness.Now.AddHours(2));

        await harness.Seed.PositionAsync(
            ride.VehicleId, PublicBffSeed.PickupLat + 0.01, PublicBffSeed.PickupLng, harness.Now);

        var body = await PublicBffHarness.OkAsync(
            await harness.GetAsync($"/public/track/{token}"), "the proxy-ride snapshot");

        AssertOnlyFields(body, ProxyFields);
        Assert.Equal("ride", body.GetProperty("kind").GetString());
        Assert.Equal("Accepted", body.GetProperty("state").GetString());

        var fare = body.GetProperty("fare");
        AssertOnlyFields(fare, ["totalMinor", "currency", "paidBy"]);

        // US-8.21: the booker chose cash, so the money is owed by whoever is in the car — and the
        // instrument is not named because there is no field on the type that could name one (P-09).
        Assert.Equal("cash_due", fare.GetProperty("paidBy").GetString());
        Assert.Equal(45_000, fare.GetProperty("totalMinor").GetInt64());
        Assert.Equal("LKR", fare.GetProperty("currency").GetString());

        // The straight line between the two ends of the journey; ADD §7.6 puts routing in Phase 3.
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("route").GetProperty("polyline").GetString()));

        // Driving towards the pickup at a plausible speed, so an estimate exists and is sane.
        Assert.True(body.GetProperty("etaMin").GetInt32() is >= 0 and <= 90);

        Assert.DoesNotContain(ride.BookerPhone, body.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_gateway_paid_proxy_ride_says_the_booker_settles_it()
    {
        await using var harness = await StartAsync();

        var ride = await harness.Seed.RideAsync(state: "InProgress", kind: 1, paymentMethod: "onepay");
        var token = await harness.Seed.TokenAsync(
            ride.RideId, "proxy_rider", harness.Now.AddHours(2));

        var body = await PublicBffHarness.OkAsync(
            await harness.GetAsync($"/public/track/{token}"), "the proxy-ride snapshot");

        Assert.Equal("booker", body.GetProperty("fare").GetProperty("paidBy").GetString());
    }

    [Fact]
    public async Task A_pickup_confirm_holder_sees_a_first_name_and_a_countdown()
    {
        await using var harness = await StartAsync();

        var (token, _, _, _) = await harness.Seed.PickupRequestAsync(
            issuedAt: harness.Now.AddSeconds(-60));

        var body = await PublicBffHarness.OkAsync(
            await harness.GetAsync($"/public/track/{token}"), "the pickup-confirm snapshot");

        AssertOnlyFields(body, PickupFields);
        Assert.Equal("pickup_confirm", body.GetProperty("kind").GetString());
        Assert.Equal("Sanduni", body.GetProperty("bookerFirstName").GetString());

        // P-02's countdown: 300 s issued, 60 s ago.
        Assert.InRange(body.GetProperty("ttlRemainingSec").GetInt32(), 235, 240);

        // The narrowest of the three, and this is what "narrowest" means: no ride, no driver, no
        // vehicle, no position and nothing about the person asking beyond their first name.
        Assert.False(body.TryGetProperty("driver", out _));
        Assert.False(body.TryGetProperty("position", out _));
        Assert.False(body.TryGetProperty("state", out _));
    }

    [Fact]
    public async Task A_stale_position_is_omitted_rather_than_drawn()
    {
        await using var harness = await StartAsync();

        var ride = await harness.Seed.RideAsync(state: "InProgress", kind: 2);
        var token = await harness.Seed.TokenAsync(
            ride.RideId, "package_recipient", harness.Now.AddHours(4));

        // Twenty minutes old. The marker has not moved and the person watching is not in the
        // vehicle, so there is no way for them to tell.
        await harness.Seed.PositionAsync(
            ride.VehicleId,
            PublicBffSeed.DropoffLat,
            PublicBffSeed.DropoffLng,
            harness.Now.AddMinutes(-20));

        var body = await PublicBffHarness.OkAsync(
            await harness.GetAsync($"/public/track/{token}"), "the package snapshot");

        Assert.False(body.TryGetProperty("position", out _));

        // With no fix there is no evidence the parcel has left the sender, so the conservative
        // answer stands rather than a guess.
        Assert.Equal("PickedUp", body.GetProperty("status").GetString());
    }

    private Task<PublicBffHarness> StartAsync() => PublicBffHarness.StartAsync(postgres, redis);

    /// <summary>
    /// Every property the response carries is one the contract declares, and no other.
    /// </summary>
    /// <remarks>
    /// The "and nothing more" half of the definition of done. A field the contract does not name is
    /// a failure whether or not anybody would have looked at it, because the thing being defended is
    /// what a token holder can see.
    /// </remarks>
    private static void AssertOnlyFields(JsonElement element, string[] allowed)
    {
        var unexpected = element.EnumerateObject()
            .Select(static property => property.Name)
            .Where(name => !allowed.Contains(name, StringComparer.Ordinal))
            .ToArray();

        Assert.True(
            unexpected.Length == 0,
            $"The response carries {string.Join(", ", unexpected)}, which the contract does not declare: "
            + element.GetRawText());
    }
}
