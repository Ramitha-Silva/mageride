using MageRide.PublicBff.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.PublicBff.Tests.Integration;

/// <summary>
/// "A web pickup-confirm resolves the same location request an in-app confirm would" (AL-45).
/// </summary>
/// <remarks>
/// <b>Asserted on the <c>rides.location_requests</c> row, through a real ride-svc.</b> A stub would
/// prove that public-bff calls what it calls; what AL-45 promises is that the row moves, and that the
/// booker's pin auto-fills from it exactly as it does for the in-app path.
/// </remarks>
[Collection<PublicBffCollection>]
public sealed class PickupConfirmTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task Sharing_a_location_confirms_the_same_row_the_app_would_and_burns_the_token()
    {
        await using var harness = await StartAsync();

        var (token, id, _, _) = await harness.Seed.PickupRequestAsync(
            issuedAt: harness.Now.AddSeconds(-30));

        var body = await PublicBffHarness.OkAsync(
            await harness.PostAsync(
                $"/public/track/{token}/pickup/confirm",
                new { lat = 6.9271, lng = 79.8612, accuracy = 12.5 }),
            "the web pickup confirm");

        Assert.Equal("Confirmed", body.GetProperty("state").GetString());

        var (state, lat, lng) = await harness.LocationRequestAsync(id);

        Assert.Equal("Confirmed", state);
        Assert.Equal(6.9271, lat!.Value, 4);
        Assert.Equal(79.8612, lng!.Value, 4);

        // BR-29.1: single use. The token is burned before the coordinate is forwarded, so a double
        // tap cannot resolve the request twice.
        var (_, _, revokedAt) = await harness.TokenMeterAsync(token);
        Assert.Equal(harness.Now, revokedAt);
    }

    [Fact]
    public async Task Declining_stores_no_coordinates_at_all()
    {
        await using var harness = await StartAsync();

        var (token, id, _, _) = await harness.Seed.PickupRequestAsync(
            issuedAt: harness.Now.AddSeconds(-30));

        var body = await PublicBffHarness.OkAsync(
            await harness.PostAsync($"/public/track/{token}/pickup/decline", body: null),
            "the web pickup decline");

        Assert.Equal("Declined", body.GetProperty("state").GetString());

        var (state, lat, lng) = await harness.LocationRequestAsync(id);

        // P-02, and it is three properties of three components rather than one reviewer's care: the
        // handler takes no body, the client sends no content, and ride-svc's statement has no
        // `resolved_geo` in its SET list.
        Assert.Equal("Declined", state);
        Assert.Null(lat);
        Assert.Null(lng);
    }

    [Fact]
    public async Task A_declined_body_carrying_coordinates_still_stores_none()
    {
        await using var harness = await StartAsync();

        var (token, id, _, _) = await harness.Seed.PickupRequestAsync(
            issuedAt: harness.Now.AddSeconds(-30));

        // A client that posts a position to the decline route anyway — a bug, or somebody probing.
        // There is nowhere for it to go.
        await PublicBffHarness.OkAsync(
            await harness.PostAsync(
                $"/public/track/{token}/pickup/decline", new { lat = 6.9271, lng = 79.8612 }),
            "a decline with a body");

        var (_, lat, lng) = await harness.LocationRequestAsync(id);

        Assert.Null(lat);
        Assert.Null(lng);
    }

    [Fact]
    public async Task A_second_tap_is_refused_rather_than_resolving_the_request_twice()
    {
        await using var harness = await StartAsync();

        var (token, id, _, _) = await harness.Seed.PickupRequestAsync(
            issuedAt: harness.Now.AddSeconds(-30));

        await PublicBffHarness.OkAsync(
            await harness.PostAsync(
                $"/public/track/{token}/pickup/confirm", new { lat = 6.9271, lng = 79.8612 }),
            "the first confirm");

        var (status, code, _) = await PublicBffHarness.ProblemAsync(
            await harness.PostAsync(
                $"/public/track/{token}/pickup/decline", body: null));

        Assert.Equal(410, status);
        Assert.Equal("token-expired-or-revoked", code);

        // And the row still says what the rider actually chose.
        var (state, _, _) = await harness.LocationRequestAsync(id);
        Assert.Equal("Confirmed", state);
    }

    [Fact]
    public async Task An_expired_request_is_refused_even_while_its_token_is_still_live()
    {
        await using var harness = await StartAsync();

        // The 300 s deadline is `issued_at + ttl_seconds` on the request row, which is what
        // ride-svc's sweep reads (0609 (c)). A token whose own expiry is generous does not extend it.
        var (token, id, _, _) = await harness.Seed.PickupRequestAsync(
            issuedAt: harness.Now.AddSeconds(-400),
            tokenExpiresAt: harness.Now.AddHours(1));

        var (status, code, _) = await PublicBffHarness.ProblemAsync(
            await harness.PostAsync(
                $"/public/track/{token}/pickup/confirm", new { lat = 6.9271, lng = 79.8612 }));

        Assert.Equal(410, status);
        Assert.Equal("token-expired-or-revoked", code);

        var (state, lat, _) = await harness.LocationRequestAsync(id);
        Assert.Equal("RiderNotRegistered", state);
        Assert.Null(lat);
    }

    [Fact]
    public async Task A_package_token_cannot_answer_a_pickup_request()
    {
        await using var harness = await StartAsync();

        var ride = await harness.Seed.RideAsync(state: "InProgress", kind: 2);
        var token = await harness.Seed.TokenAsync(
            ride.RideId, "package_recipient", harness.Now.AddHours(4));

        var (status, code, _) = await PublicBffHarness.ProblemAsync(
            await harness.PostAsync(
                $"/public/track/{token}/pickup/confirm", new { lat = 6.9271, lng = 79.8612 }));

        // 403 rather than the family's 404: the holder already knows their link is live, because the
        // snapshot answers. There is no oracle in telling them this particular door is not theirs.
        Assert.Equal(403, status);
        Assert.Equal("forbidden", code);
    }

    [Fact]
    public async Task An_unconfigured_ride_svc_is_a_503_on_a_route_that_still_exists()
    {
        await using var harness = await PublicBffHarness.StartAsync(
            postgres, redis, withRideService: false);

        var (token, _, _, _) = await harness.Seed.PickupRequestAsync(
            issuedAt: harness.Now.AddSeconds(-30));

        var (status, code, _) = await PublicBffHarness.ProblemAsync(
            await harness.PostAsync(
                $"/public/track/{token}/pickup/confirm", new { lat = 6.9271, lng = 79.8612 }));

        Assert.Equal(503, status);
        Assert.Equal("dependency-unavailable", code);

        // The route is still in the table, still anonymous, still gated on the token. A route that
        // vanished with a setting is a route no fence test enumerates.
        Assert.Contains(
            harness.Routes(),
            route => route.Route == "/public/track/{token}/pickup/confirm" && route.InPublicGroup);
    }

    [Fact]
    public async Task Validation_refuses_a_confirm_with_no_coordinates()
    {
        await using var harness = await StartAsync();

        var (token, id, _, _) = await harness.Seed.PickupRequestAsync(
            issuedAt: harness.Now.AddSeconds(-30));

        var (status, code, _) = await PublicBffHarness.ProblemAsync(
            await harness.PostAsync($"/public/track/{token}/pickup/confirm", new { accuracy = 5.0 }));

        Assert.Equal(400, status);
        Assert.Equal("validation-failed", code);

        // Refused before the token is burned, so the rider can try again.
        var (_, _, revokedAt) = await harness.TokenMeterAsync(token);
        Assert.Null(revokedAt);

        var (state, _, _) = await harness.LocationRequestAsync(id);
        Assert.Equal("RiderNotRegistered", state);
    }

    private Task<PublicBffHarness> StartAsync() => PublicBffHarness.StartAsync(postgres, redis);
}
