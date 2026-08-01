using MageRide.PublicBff.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.PublicBff.Tests.Integration;

/// <summary>
/// The uniform 404 / 410 / 429, the metering, and "a dead token returns zero ride data".
/// </summary>
[Collection<PublicBffCollection>]
public sealed class TokenGateTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task An_unknown_token_is_a_404_on_every_route_of_the_family()
    {
        await using var harness = await StartAsync();

        var unknown = new string('a', 43);

        foreach (var path in new[]
                 {
                     $"/public/track/{unknown}",
                     $"/public/track/{unknown}/live?since=0.",
                     $"/public/track/{unknown}/receipt",
                 })
        {
            var (status, code, _) = await PublicBffHarness.ProblemAsync(await harness.GetAsync(path));

            Assert.Equal(404, status);
            Assert.Equal("token-unknown", code);
        }

        foreach (var path in new[]
                 {
                     $"/public/track/{unknown}/pickup/decline",
                     $"/public/track/{unknown}/sos",
                 })
        {
            var (status, code, _) = await PublicBffHarness.ProblemAsync(
                await harness.PostAsync(path, path.EndsWith("sos", StringComparison.Ordinal)
                    ? new { lat = 6.9, lng = 79.8 }
                    : null));

            Assert.Equal(404, status);
            Assert.Equal("token-unknown", code);
        }
    }

    [Fact]
    public async Task An_expired_token_answers_410_with_nothing_about_the_ride_in_the_body()
    {
        await using var harness = await StartAsync();

        var ride = await harness.Seed.RideAsync(state: "InProgress", kind: 2);
        var token = await harness.Seed.TokenAsync(
            ride.RideId, "package_recipient", harness.Now.AddMinutes(-1));

        await harness.Seed.PositionAsync(
            ride.VehicleId, PublicBffSeed.DropoffLat, PublicBffSeed.DropoffLng, harness.Now);

        var (status, code, body) = await PublicBffHarness.ProblemAsync(
            await harness.GetAsync($"/public/track/{token}"));

        Assert.Equal(410, status);
        Assert.Equal("token-expired-or-revoked", code);

        // The fence is the BODY, not the status code. The 410 is produced before the ride is read
        // at all, so there is no code path on which a dead token could carry any of this.
        Assert.DoesNotContain(ride.RideId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ride.DriverPhone, body, StringComparison.Ordinal);
        Assert.DoesNotContain(ride.BookerPhone, body, StringComparison.Ordinal);
        Assert.DoesNotContain("Kasun", body, StringComparison.Ordinal);
        Assert.DoesNotContain("InProgress", body, StringComparison.Ordinal);
        Assert.DoesNotContain(PublicBffSeed.DropoffLat.ToString(System.Globalization.CultureInfo.InvariantCulture), body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_revoked_token_is_a_410_too()
    {
        await using var harness = await StartAsync();

        var ride = await harness.Seed.RideAsync(state: "InProgress", kind: 1);
        var token = await harness.Seed.TokenAsync(
            ride.RideId,
            "proxy_rider",
            harness.Now.AddHours(2),
            revokedAt: harness.Now.AddMinutes(-5));

        var (status, code, _) = await PublicBffHarness.ProblemAsync(
            await harness.GetAsync($"/public/track/{token}"));

        Assert.Equal(410, status);
        Assert.Equal("token-expired-or-revoked", code);
    }

    [Fact]
    public async Task A_trip_view_token_belongs_to_safety_svc_and_is_refused_as_unknown_here()
    {
        await using var harness = await StartAsync();

        var ride = await harness.Seed.RideAsync(state: "InProgress", kind: 0);
        var token = await harness.Seed.TokenAsync(
            ride.RideId, "trip_view", harness.Now.AddHours(2));

        var (status, code, body) = await PublicBffHarness.ProblemAsync(
            await harness.GetAsync($"/public/track/{token}"));

        // D-34's share link has its own contract on safety-svc with its own redaction. Serving it
        // here would answer with the wrong shape — and the refusal is the unknown-token one, so the
        // route cannot be used as an oracle over which links are live.
        Assert.Equal(404, status);
        Assert.Equal("token-unknown", code);
        Assert.DoesNotContain("trip_view", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dead_token_is_metered_before_it_is_refused()
    {
        await using var harness = await StartAsync();

        var ride = await harness.Seed.RideAsync(state: "InProgress", kind: 2);
        var token = await harness.Seed.TokenAsync(
            ride.RideId, "package_recipient", harness.Now.AddMinutes(-1));

        (await harness.GetAsync($"/public/track/{token}")).Dispose();
        (await harness.GetAsync($"/public/track/{token}")).Dispose();
        (await harness.GetAsync($"/public/track/{token}")).Dispose();

        var (accessCount, lastAccessAt, _) = await harness.TokenMeterAsync(token);

        // AL-44's metering exists precisely for this: somebody still hammering a link that died is
        // the pattern an unauthenticated surface has no other way to surface.
        Assert.Equal(3, accessCount);
        Assert.Equal(harness.Now, lastAccessAt);
    }

    [Fact]
    public async Task The_per_token_bucket_answers_429_with_a_retry_hint()
    {
        await using var harness = await StartAsync(new Dictionary<string, string?>
        {
            ["PublicBff:PerTokenPerMinute"] = "3",
        });

        var ride = await harness.Seed.RideAsync(state: "InProgress", kind: 2);
        var token = await harness.Seed.TokenAsync(
            ride.RideId, "package_recipient", harness.Now.AddHours(4));

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var allowed = await harness.GetAsync($"/public/track/{token}", clientIp: "203.0.113.10");
            Assert.Equal(200, (int)allowed.StatusCode);
        }

        var (status, code, body) = await PublicBffHarness.ProblemAsync(
            await harness.GetAsync($"/public/track/{token}", clientIp: "203.0.113.10"));

        Assert.Equal(429, status);
        Assert.Equal("rate-limited", code);
        Assert.Contains("retryAfterSeconds", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_per_ip_bucket_holds_across_different_tokens()
    {
        await using var harness = await StartAsync(new Dictionary<string, string?>
        {
            ["PublicBff:PerTokenPerMinute"] = "60",
            ["PublicBff:PerIpPerMinute"] = "2",
        });

        var ride = await harness.Seed.RideAsync(state: "InProgress", kind: 2);

        var first = await harness.Seed.TokenAsync(
            ride.RideId, "package_recipient", harness.Now.AddHours(4));

        var second = await harness.Seed.TokenAsync(
            ride.RideId, "package_recipient", harness.Now.AddHours(4));

        var third = await harness.Seed.TokenAsync(
            ride.RideId, "package_recipient", harness.Now.AddHours(4));

        const string Harvester = "198.51.100.7";

        (await harness.GetAsync($"/public/track/{first}", clientIp: Harvester)).Dispose();
        (await harness.GetAsync($"/public/track/{second}", clientIp: Harvester)).Dispose();

        // A per-token limit is no limit at all against somebody holding a hundred harvested links,
        // which is the whole reason the second bucket exists.
        var (status, code, _) = await PublicBffHarness.ProblemAsync(
            await harness.GetAsync($"/public/track/{third}", clientIp: Harvester));

        Assert.Equal(429, status);
        Assert.Equal("rate-limited", code);

        // Somebody else's connection is unaffected.
        using var otherClient = await harness.GetAsync($"/public/track/{third}", clientIp: "198.51.100.8");
        Assert.Equal(200, (int)otherClient.StatusCode);
    }

    [Fact]
    public async Task A_token_that_is_too_short_to_have_been_minted_costs_no_round_trip()
    {
        await using var harness = await StartAsync(new Dictionary<string, string?>
        {
            ["PublicBff:PerIpPerMinute"] = "1",
        });

        // Refused on shape, so the per-IP bucket is never touched — which is what stops a short-key
        // probe from spending a real visitor's budget.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var (status, code, _) = await PublicBffHarness.ProblemAsync(
                await harness.GetAsync("/public/track/short", clientIp: "192.0.2.9"));

            Assert.Equal(404, status);
            Assert.Equal("token-unknown", code);
        }
    }

    private Task<PublicBffHarness> StartAsync(IDictionary<string, string?>? settings = null) =>
        PublicBffHarness.StartAsync(postgres, redis, settings);
}
