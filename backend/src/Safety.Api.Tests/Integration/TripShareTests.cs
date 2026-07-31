using System.Net;
using MageRide.Safety.Domain;
using MageRide.Safety.Endpoints;
using MageRide.Safety.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Safety.Tests.Integration;

/// <summary>
/// D-34: a trip-scoped, TTL-bounded, revocable, rate-limited link — and no replay.
/// </summary>
[Collection(SafetyCollection.Name)]
public sealed class TripShareTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task A_passenger_shares_a_live_trip_and_the_public_view_shows_where_it_is_now()
    {
        await using var harness = await SafetyHarness.StartAsync(postgres, redis);

        var passenger = await harness.Seed.UserAsync();
        var driver = await harness.Seed.UserAsync(role: "driver");
        var vehicleId = await harness.Seed.VehicleAsync(driver.Id);
        var rideId = await harness.Seed.RideAsync(passenger.Id, driver.Id, vehicleId);

        await SafetySeed.PositionAsync(redis, vehicleId, 6.9271, 79.8612, SafetyHarness.DefaultNow);

        using var issued = await harness.PostAsync(
            $"/v1/trip-share/{rideId}", body: null, harness.Tokens.Passenger(passenger.Id));

        var share = await SafetyHarness.OkAsync<TripShareResponse>(issued, "POST /v1/trip-share");

        Assert.StartsWith(SafetyHarness.ShareBaseUrl, share.Url, StringComparison.Ordinal);
        Assert.Contains(share.Token, share.Url, StringComparison.Ordinal);

        var stored = Assert.Single(await harness.ShareTokensAsync());

        Assert.Equal(ShareTokenScopes.TripView, stored.Scope);
        Assert.Equal(rideId, stored.TripId);
        Assert.Equal(0, stored.AccessCount);

        // No bearer: the token is the credential.
        using var read = await harness.GetAsync($"/v1/trip-share/public/{share.Token}");
        var view = await SafetyHarness.OkAsync<SharedTripResponse>(read, "public view");

        Assert.Equal("InProgress", view.State);
        Assert.NotNull(view.Position);
        Assert.Equal(6.9271, view.Position!.Lat, 4);
        Assert.Equal("Nimal", view.DriverName);
        Assert.NotNull(view.Vehicle);

        // Metered on every hit — a shared link is unauthenticated, so the count is the only
        // forensic trail there is (AL-44).
        Assert.Equal(1, (await harness.ShareTokensAsync()).Single().AccessCount);
    }

    /// <summary>The third definition of done: a dead token carries no ride data at all.</summary>
    [Fact]
    public async Task A_revoked_token_answers_410_with_nothing_about_the_trip_in_the_body()
    {
        await using var harness = await SafetyHarness.StartAsync(postgres, redis);

        var passenger = await harness.Seed.UserAsync();
        var driver = await harness.Seed.UserAsync(role: "driver");
        var vehicleId = await harness.Seed.VehicleAsync(driver.Id);
        var rideId = await harness.Seed.RideAsync(passenger.Id, driver.Id, vehicleId);

        await SafetySeed.PositionAsync(redis, vehicleId, 6.9271, 79.8612, SafetyHarness.DefaultNow);

        using var issued = await harness.PostAsync(
            $"/v1/trip-share/{rideId}", body: null, harness.Tokens.Passenger(passenger.Id));

        var share = await SafetyHarness.OkAsync<TripShareResponse>(issued, "POST /v1/trip-share");

        using (var revoked = await harness.DeleteAsync(
                   $"/v1/trip-share/{rideId}", harness.Tokens.Passenger(passenger.Id)))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        }

        using var read = await harness.GetAsync($"/v1/trip-share/public/{share.Token}");

        Assert.Equal(HttpStatusCode.Gone, read.StatusCode);

        var (code, body) = await SafetyHarness.ProblemAsync(read);

        Assert.Equal("token-expired-or-revoked", code);

        // Zero ride data. The 410 is produced before the ride row is read at all, so there is no
        // code path on which a dead token could carry a position.
        Assert.DoesNotContain("6.927", body, StringComparison.Ordinal);
        Assert.DoesNotContain("79.86", body, StringComparison.Ordinal);
        Assert.DoesNotContain("InProgress", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Nimal", body, StringComparison.Ordinal);
        Assert.DoesNotContain(rideId.ToString(), body, StringComparison.Ordinal);

        // And the hit was still counted — somebody still holding a dead link is exactly the pattern
        // AL-44's metering exists to surface.
        Assert.Equal(1, (await harness.ShareTokensAsync()).Single().AccessCount);
    }

    /// <summary>An expired token is the same answer, reached a different way.</summary>
    [Fact]
    public async Task An_expired_token_answers_410()
    {
        await using var harness = await SafetyHarness.StartAsync(
            postgres, redis, new Dictionary<string, string?> { ["Safety:ShareMaxLifetime"] = "01:00:00" });

        var passenger = await harness.Seed.UserAsync();
        var rideId = await harness.Seed.RideAsync(passenger.Id);

        using var issued = await harness.PostAsync(
            $"/v1/trip-share/{rideId}", body: null, harness.Tokens.Passenger(passenger.Id));

        var share = await SafetyHarness.OkAsync<TripShareResponse>(issued, "POST /v1/trip-share");

        harness.Clock.Advance(TimeSpan.FromHours(2));

        using var read = await harness.GetAsync($"/v1/trip-share/public/{share.Token}");

        Assert.Equal(HttpStatusCode.Gone, read.StatusCode);
    }

    /// <summary>
    /// D-34's window is "trip end + 1 h", and the trip's end is somebody else's news — ride-svc
    /// calls the internal hook, which closes every trip-scoped token whoever minted it.
    /// </summary>
    [Fact]
    public async Task The_trip_end_hook_closes_every_trip_scoped_link()
    {
        await using var harness = await SafetyHarness.StartAsync(postgres, redis);

        var passenger = await harness.Seed.UserAsync();
        var rideId = await harness.Seed.RideAsync(passenger.Id);

        using (var issued = await harness.PostAsync(
                   $"/v1/trip-share/{rideId}", body: null, harness.Tokens.Passenger(passenger.Id)))
        {
            Assert.Equal(HttpStatusCode.Created, issued.StatusCode);
        }

        // A package-recipient link on the same ride, as notification-svc would have minted it: the
        // hook closes what this service did not issue, because the window is a fact about the trip.
        await using (var connection = await harness.OpenAsync())
        {
            await Dapper.SqlMapper.ExecuteAsync(
                connection,
                """
                INSERT INTO safety.trip_share_tokens (token, trip_id, scope, expires_at)
                VALUES ('package-token-for-c052', @TripId, 'package_recipient', now() + interval '4 hours');
                """,
                new { TripId = rideId });
        }

        using var closed = await harness.InternalAsync(HttpMethod.Post, $"/v1/internal/safety/trips/{rideId}/close");
        var result = await SafetyHarness.OkAsync<CloseTripSharesResponse>(closed, "trip close");

        Assert.Equal(2, result.Revoked);

        var tokens = await harness.ShareTokensAsync();

        Assert.All(tokens, token => Assert.NotNull(token.RevokedAt));

        // The grace is on the revocation instant: D-34 gives the link an hour past the trip.
        Assert.All(tokens, token => Assert.Equal(SafetyHarness.DefaultNow.AddHours(1), token.RevokedAt));
    }

    /// <summary>Re-issuing replays: two live links for one trip would make "revoke" a lie.</summary>
    [Fact]
    public async Task Re_issuing_returns_the_live_link_rather_than_minting_a_second()
    {
        await using var harness = await SafetyHarness.StartAsync(postgres, redis);

        var passenger = await harness.Seed.UserAsync();
        var rideId = await harness.Seed.RideAsync(passenger.Id);
        var bearer = harness.Tokens.Passenger(passenger.Id);

        var first = await SafetyHarness.OkAsync<TripShareResponse>(
            await harness.PostAsync($"/v1/trip-share/{rideId}", null, bearer), "first issue");

        var second = await SafetyHarness.OkAsync<TripShareResponse>(
            await harness.PostAsync($"/v1/trip-share/{rideId}", null, bearer), "second issue");

        Assert.Equal(first.Token, second.Token);
        Assert.Single(await harness.ShareTokensAsync());
    }

    /// <summary>Only a party to the trip may share it, and only they may revoke it.</summary>
    [Fact]
    public async Task A_stranger_can_neither_share_nor_revoke_somebody_elses_trip()
    {
        await using var harness = await SafetyHarness.StartAsync(postgres, redis);

        var passenger = await harness.Seed.UserAsync();
        var stranger = await harness.Seed.UserAsync();
        var rideId = await harness.Seed.RideAsync(passenger.Id);

        using (var issue = await harness.PostAsync(
                   $"/v1/trip-share/{rideId}", null, harness.Tokens.Passenger(stranger.Id)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, issue.StatusCode);

            var (code, _) = await SafetyHarness.ProblemAsync(issue);
            Assert.Equal("not-ride-participant", code);
        }

        using var revoke = await harness.DeleteAsync(
            $"/v1/trip-share/{rideId}", harness.Tokens.Passenger(stranger.Id));

        Assert.Equal(HttpStatusCode.Forbidden, revoke.StatusCode);
    }

    /// <summary>A trip that has ended has nothing live to show, and the view is live-only.</summary>
    [Fact]
    public async Task A_terminal_trip_cannot_be_shared()
    {
        await using var harness = await SafetyHarness.StartAsync(postgres, redis);

        var passenger = await harness.Seed.UserAsync();
        var rideId = await harness.Seed.RideAsync(passenger.Id);

        await harness.Seed.EndRideAsync(rideId);

        using var response = await harness.PostAsync(
            $"/v1/trip-share/{rideId}", null, harness.Tokens.Passenger(passenger.Id));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var (code, _) = await SafetyHarness.ProblemAsync(response);
        Assert.Equal("ride-terminal", code);
    }

    /// <summary>
    /// A position older than <c>Safety:PositionMaxAge</c> is omitted, not drawn. The person watching
    /// is not in the vehicle and has no other way to tell that the marker stopped moving.
    /// </summary>
    [Fact]
    public async Task A_stale_position_is_omitted_rather_than_drawn()
    {
        await using var harness = await SafetyHarness.StartAsync(postgres, redis);

        var passenger = await harness.Seed.UserAsync();
        var driver = await harness.Seed.UserAsync(role: "driver");
        var vehicleId = await harness.Seed.VehicleAsync(driver.Id);
        var rideId = await harness.Seed.RideAsync(passenger.Id, driver.Id, vehicleId);

        await SafetySeed.PositionAsync(
            redis, vehicleId, 6.9271, 79.8612, SafetyHarness.DefaultNow.AddMinutes(-30));

        var share = await SafetyHarness.OkAsync<TripShareResponse>(
            await harness.PostAsync($"/v1/trip-share/{rideId}", null, harness.Tokens.Passenger(passenger.Id)),
            "issue");

        using var read = await harness.GetAsync($"/v1/trip-share/public/{share.Token}");
        var view = await SafetyHarness.OkAsync<SharedTripResponse>(read, "public view");

        Assert.Null(view.Position);
        Assert.Equal("InProgress", view.State);
    }

    /// <summary>D-34: 60 reads a minute per token, held in Redis so every replica shares the count.</summary>
    [Fact]
    public async Task A_share_link_is_rate_limited_per_token()
    {
        await using var harness = await SafetyHarness.StartAsync(
            postgres,
            redis,
            new Dictionary<string, string?>
            {
                ["Safety:PublicViewPerMinute"] = "3",
                ["Safety:PublicViewPerMinutePerIp"] = "1000",
            });

        var passenger = await harness.Seed.UserAsync();
        var rideId = await harness.Seed.RideAsync(passenger.Id);

        var share = await SafetyHarness.OkAsync<TripShareResponse>(
            await harness.PostAsync($"/v1/trip-share/{rideId}", null, harness.Tokens.Passenger(passenger.Id)),
            "issue");

        for (var i = 0; i < 3; i++)
        {
            using var allowed = await harness.GetAsync($"/v1/trip-share/public/{share.Token}");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using var refused = await harness.GetAsync($"/v1/trip-share/public/{share.Token}");

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);

        var (code, _) = await SafetyHarness.ProblemAsync(refused);
        Assert.Equal("rate-limited", code);
    }

    /// <summary>
    /// The per-IP companion: a per-token limit alone is no limit against somebody who has harvested
    /// a hundred links.
    /// </summary>
    [Fact]
    public async Task Reads_are_also_limited_per_address_across_tokens()
    {
        await using var harness = await SafetyHarness.StartAsync(
            postgres,
            redis,
            new Dictionary<string, string?>
            {
                ["Safety:PublicViewPerMinute"] = "1000",
                ["Safety:PublicViewPerMinutePerIp"] = "2",
            });

        var tokens = new List<string>();

        // One passenger per ride: `ux_rides_open_passenger` (C004) allows one non-terminal ride per
        // account, and three shareable trips means three passengers.
        for (var i = 0; i < 3; i++)
        {
            var passenger = await harness.Seed.UserAsync();
            var rideId = await harness.Seed.RideAsync(passenger.Id);

            tokens.Add((await SafetyHarness.OkAsync<TripShareResponse>(
                await harness.PostAsync(
                    $"/v1/trip-share/{rideId}", null, harness.Tokens.Passenger(passenger.Id)),
                "issue")).Token);
        }

        const string Ip = "203.0.113.7";

        for (var i = 0; i < 2; i++)
        {
            using var allowed = await harness.GetAsync($"/v1/trip-share/public/{tokens[i]}", clientIp: Ip);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        // A third, previously untouched token from the same address.
        using var refused = await harness.GetAsync($"/v1/trip-share/public/{tokens[2]}", clientIp: Ip);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    /// <summary>An unknown token is a 404 and never an oracle for which tokens exist.</summary>
    [Fact]
    public async Task An_unknown_token_is_not_found()
    {
        await using var harness = await SafetyHarness.StartAsync(postgres, redis);

        using var response = await harness.GetAsync("/v1/trip-share/public/never-issued-anywhere");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var (code, _) = await SafetyHarness.ProblemAsync(response);
        Assert.Equal("token-unknown", code);
    }

    /// <summary>
    /// An AL-44 scope belongs to public-bff's richer family, not to this one — serving it here
    /// would answer with the wrong shape and skip that family's per-scope redaction.
    /// </summary>
    [Fact]
    public async Task A_package_recipient_token_is_not_served_by_this_surface()
    {
        await using var harness = await SafetyHarness.StartAsync(postgres, redis);

        var passenger = await harness.Seed.UserAsync();
        var rideId = await harness.Seed.RideAsync(passenger.Id);

        await using (var connection = await harness.OpenAsync())
        {
            await Dapper.SqlMapper.ExecuteAsync(
                connection,
                """
                INSERT INTO safety.trip_share_tokens (token, trip_id, scope, expires_at)
                VALUES ('package-scope-token', @TripId, 'package_recipient', now() + interval '4 hours');
                """,
                new { TripId = rideId });
        }

        using var response = await harness.GetAsync("/v1/trip-share/public/package-scope-token");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
