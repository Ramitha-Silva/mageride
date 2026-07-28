using System.Net;
using Dapper;
using MageRide.Iam.Tests.Infrastructure;
using MageRide.Shared.Caching;
using MageRide.Shared.Persistence;
using MageRide.TestKit;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace MageRide.Iam.Tests.Integration;

/// <summary>
/// C020's third definition-of-done item and the rest of D-29: single active device per app,
/// single-use rotating refresh, replay detection, logout.
/// </summary>
[Collection<IamCollection>]
public sealed class SessionLifecycleTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task A_second_sign_in_revokes_the_prior_session_for_that_app_and_only_that_app()
    {
        await using var harness = await IamHarness.StartWithoutResendCooldownAsync(postgres, redis);
        var phone = IamHarness.NextPhone();

        // AL-08 / US-1.12: the same person runs the Driver App and the Passenger App at once.
        var passengerOnPhoneA = await harness.SignInAsync(phone, "phone-a", "passenger");
        var driver = await harness.SignInAsync(phone, "tablet", "driver");
        await harness.SignInAsync(phone, "phone-b", "passenger");

        var supersededRefresh = await harness.PostAsync(
            "/v1/auth/refresh", new { refreshToken = passengerOnPhoneA.RefreshToken });
        await ProblemDocument.AssertAsync(supersededRefresh, HttpStatusCode.Unauthorized, "unauthorized");

        // The driver session was never touched — that is the whole point of "per app".
        var driverRefresh = await harness.PostAsync("/v1/auth/refresh", new { refreshToken = driver.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, driverRefresh.StatusCode);
    }

    [Fact]
    public async Task At_most_one_session_per_user_and_app_is_ever_active()
    {
        await using var harness = await IamHarness.StartWithoutResendCooldownAsync(postgres, redis);
        var phone = IamHarness.NextPhone();

        await harness.SignInAsync(phone, "d1", "passenger");
        await harness.SignInAsync(phone, "d2", "passenger");
        await harness.SignInAsync(phone, "d3", "driver");

        var factory = harness.Services.GetRequiredService<INpgsqlConnectionFactory>();
        await using var connection = await factory.OpenAsync();

        var active = await connection.QueryAsync<string>(
            """
            SELECT app FROM iam.sessions
             WHERE user_id = (SELECT id FROM iam.users WHERE phone = @Phone)
               AND revoked_at IS NULL
             ORDER BY app;
            """,
            new { Phone = phone });

        Assert.Equal(["driver", "passenger"], active);
    }

    [Fact]
    public async Task A_refresh_rotates_the_pair_and_the_spent_token_stops_working()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var session = await harness.SignInAsync(IamHarness.NextPhone(), "device-rotate");

        var response = await harness.PostAsync("/v1/auth/refresh", new { refreshToken = session.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rotated = await IamHarness.ReadJsonAsync(response);
        var newRefresh = rotated.GetProperty("refreshToken").GetString();

        Assert.Equal(1800, rotated.GetProperty("expiresIn").GetInt32());
        Assert.NotEqual(session.RefreshToken, newRefresh);
        Assert.False(string.IsNullOrWhiteSpace(rotated.GetProperty("accessToken").GetString()));

        // Single-use (D-29). Replaying the spent one also takes its successor down with it, which
        // is the point: we cannot tell a client bug from a stolen token, so the lineage ends.
        var replay = await harness.PostAsync("/v1/auth/refresh", new { refreshToken = session.RefreshToken });
        await ProblemDocument.AssertAsync(replay, HttpStatusCode.Unauthorized, "unauthorized");

        var successorAfterReplay = await harness.PostAsync("/v1/auth/refresh", new { refreshToken = newRefresh });
        await ProblemDocument.AssertAsync(successorAfterReplay, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Replaying_a_token_from_an_older_sign_in_does_not_end_the_newer_session()
    {
        await using var harness = await IamHarness.StartWithoutResendCooldownAsync(postgres, redis);
        var phone = IamHarness.NextPhone();

        // The livelock 0106 exists to prevent: an old handset polling refresh in the background
        // must not be able to log the handset the user actually holds out, over and over.
        var old = await harness.SignInAsync(phone, "old-handset");
        var current = await harness.SignInAsync(phone, "new-handset");

        var replay = await harness.PostAsync("/v1/auth/refresh", new { refreshToken = old.RefreshToken });
        await ProblemDocument.AssertAsync(replay, HttpStatusCode.Unauthorized, "unauthorized");

        var stillGood = await harness.PostAsync("/v1/auth/refresh", new { refreshToken = current.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, stillGood.StatusCode);
    }

    [Fact]
    public async Task The_refresh_token_is_also_accepted_as_a_bearer_credential()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var session = await harness.SignInAsync(IamHarness.NextPhone(), "device-bearer");

        // The contract declares a `refreshToken` bearer scheme and the body field; C013 sends both.
        var response = await harness.PostAsync("/v1/auth/refresh", new { }, bearer: session.RefreshToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("mr1.AAAAAAAAAAAAAAAAAAAAAA.ZZZZ")]
    public async Task A_forged_refresh_token_is_401(string token)
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var response = await harness.PostAsync("/v1/auth/refresh", new { refreshToken = token });

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Logout_revokes_the_session_and_is_idempotent()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var session = await harness.SignInAsync(IamHarness.NextPhone(), "device-logout");

        var first = await harness.PostAsync("/v1/auth/logout", new { }, bearer: session.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        var afterLogout = await harness.PostAsync("/v1/auth/refresh", new { refreshToken = session.RefreshToken });
        await ProblemDocument.AssertAsync(afterLogout, HttpStatusCode.Unauthorized, "unauthorized");

        // "Already-revoked sessions also answer 204" (US-1.7). A new key, so this is a second
        // execution rather than an idempotent replay.
        var second = await harness.PostAsync("/v1/auth/logout", new { }, bearer: session.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
    }

    [Fact]
    public async Task Logout_ends_only_the_app_the_caller_is_signed_in_to()
    {
        await using var harness = await IamHarness.StartWithoutResendCooldownAsync(postgres, redis);
        var phone = IamHarness.NextPhone();

        var passenger = await harness.SignInAsync(phone, "phone", "passenger");
        var driver = await harness.SignInAsync(phone, "tablet", "driver");

        await harness.PostAsync("/v1/auth/logout", new { }, bearer: passenger.AccessToken);

        var driverStillWorks = await harness.PostAsync("/v1/auth/refresh", new { refreshToken = driver.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, driverStillWorks.StatusCode);
    }

    [Fact]
    public async Task The_session_is_mirrored_into_redis_and_dropped_again_on_revocation()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var phone = IamHarness.NextPhone();

        var session = await harness.SignInAsync(phone, "device-redis");

        var factory = harness.Services.GetRequiredService<INpgsqlConnectionFactory>();
        Guid jti;
        await using (var connection = await factory.OpenAsync())
        {
            jti = await connection.ExecuteScalarAsync<Guid>(
                """
                SELECT jti FROM iam.sessions
                 WHERE user_id = (SELECT id FROM iam.users WHERE phone = @Phone) AND revoked_at IS NULL;
                """,
                new { Phone = phone });
        }

        // ADD §12.1: iam.sessions is the record, Redis refresh:{jti} is the O(1) revocation lookup.
        var database = harness.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        Assert.True(await database.KeyExistsAsync(RedisKeys.RefreshToken(jti.ToString())));

        await harness.PostAsync("/v1/auth/logout", new { }, bearer: session.AccessToken);

        Assert.False(await database.KeyExistsAsync(RedisKeys.RefreshToken(jti.ToString())));
    }
}
