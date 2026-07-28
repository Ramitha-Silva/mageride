using System.Net;
using System.Net.Http.Json;
using Dapper;
using MageRide.Iam.Tests.Infrastructure;
using MageRide.Shared.Auth;
using MageRide.Shared.Persistence;
using MageRide.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MageRide.Iam.Tests.Integration;

/// <summary>
/// C020's first two definition-of-done items: a passenger and a driver obtain a token pair with a
/// +94 number, and the access token is RS256, 30 minutes, and verifiable against the published
/// JWKS.
/// </summary>
[Collection<IamCollection>]
public sealed class SignInTests(PostgresFixture postgres, RedisFixture redis)
{
    [Theory]
    [InlineData("passenger", MageRideRoles.Passenger)]
    [InlineData("driver", MageRideRoles.Driver)]
    public async Task An_app_user_signs_in_with_a_plus94_number_and_gets_a_token_pair(string app, string expectedRole)
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var phone = IamHarness.NextPhone();

        var session = await harness.SignInAsync(phone, "device-" + app, app);

        Assert.True(session.IsNewUser);
        Assert.Equal(expectedRole, session.Role);
        Assert.Equal(1800, session.ExpiresIn);
        Assert.False(string.IsNullOrWhiteSpace(session.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(session.RefreshToken));

        var token = new JsonWebToken(session.AccessToken);
        Assert.Equal(session.UserId, token.GetClaim(MageRideClaims.Subject).Value);
        Assert.Equal(app, token.GetClaim(MageRideClaims.App).Value);
        Assert.Equal("device-" + app, token.GetClaim(MageRideClaims.DeviceId).Value);
        Assert.Equal(expectedRole, token.GetClaim(MageRideClaims.Role).Value);
    }

    [Fact]
    public async Task The_access_token_is_rs256_expires_in_thirty_minutes_and_validates_against_the_published_jwks()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var session = await harness.SignInAsync(IamHarness.NextPhone(), "device-jwks");

        // Fetched over HTTP exactly as the gateway, EMQX and every other service fetch it (D-21).
        var jwksResponse = await harness.Client.GetAsync("/.well-known/jwks.json");
        Assert.Equal(HttpStatusCode.OK, jwksResponse.StatusCode);

        var keySet = JsonWebKeySet.Create(await jwksResponse.Content.ReadAsStringAsync());
        var parsed = new JsonWebToken(session.AccessToken);

        Assert.Equal(SecurityAlgorithms.RsaSha256, parsed.Alg);
        Assert.Equal(TimeSpan.FromMinutes(30), parsed.ValidTo - parsed.ValidFrom);
        Assert.Equal(keySet.Keys.Single().Kid, parsed.Kid);

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(session.AccessToken, new TokenValidationParameters
        {
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            IssuerSigningKeys = keySet.GetSigningKeys(),
            ValidIssuer = "https://iam.mageride.lk",
            ValidateAudience = false,
        });

        Assert.True(result.IsValid, result.Exception?.ToString());
    }

    [Fact]
    public async Task The_jwks_is_public_and_says_how_long_to_cache_it()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var response = await harness.Client.GetAsync("/.well-known/jwks.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // D-21: 15 minutes at the gateway, EMQX and fanout-svc.
        Assert.Equal(TimeSpan.FromMinutes(15), response.Headers.CacheControl?.MaxAge);
        Assert.True(response.Headers.CacheControl?.Public);
    }

    [Fact]
    public async Task The_second_sign_in_of_the_same_number_is_not_a_new_user()
    {
        await using var harness = await IamHarness.StartWithoutResendCooldownAsync(postgres, redis);
        var phone = IamHarness.NextPhone();

        var first = await harness.SignInAsync(phone, "device-1");
        var second = await harness.SignInAsync(phone, "device-2");

        Assert.True(first.IsNewUser);
        Assert.False(second.IsNewUser);
        Assert.Equal(first.UserId, second.UserId);
    }

    [Fact]
    public async Task Opening_the_driver_app_does_not_hand_an_existing_passenger_the_driver_role()
    {
        await using var harness = await IamHarness.StartWithoutResendCooldownAsync(postgres, redis);
        var phone = IamHarness.NextPhone();

        await harness.SignInAsync(phone, "device-p", "passenger");
        var asDriver = await harness.SignInAsync(phone, "device-d", "driver");

        // The app claim follows the surface, but the role set does not: holding the driver role
        // is what registry-svc onboarding grants (C029), not what opening an app does.
        Assert.Equal(MageRideRoles.Passenger, asDriver.Role);
        Assert.Equal("driver", new JsonWebToken(asDriver.AccessToken).GetClaim(MageRideClaims.App).Value);
    }

    [Fact]
    public async Task The_sign_in_writes_the_user_the_device_and_the_session()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var phone = IamHarness.NextPhone();

        var session = await harness.SignInAsync(phone, "device-rows", "driver");

        var factory = harness.Services.GetRequiredService<INpgsqlConnectionFactory>();
        await using var connection = await factory.OpenAsync();

        var stored = await connection.QuerySingleAsync<StoredSession>(
            """
            SELECT s.user_id, s.app, d.device_key, d.platform
              FROM iam.sessions s
              JOIN iam.devices d ON d.id = s.device_id
             WHERE s.user_id = (SELECT id FROM iam.users WHERE phone = @Phone)
               AND s.revoked_at IS NULL;
            """,
            new { Phone = phone });

        Assert.Equal(Guid.Parse(session.UserId), stored.UserId);
        Assert.Equal("driver", stored.App);
        Assert.Equal("device-rows", stored.DeviceKey);
        Assert.Equal("android", stored.Platform);

        // The OTP is never at rest in the clear (iam.otp_attempts.otp_hash).
        var storedCode = await connection.ExecuteScalarAsync<byte[]>(
            "SELECT otp_hash FROM iam.otp_attempts WHERE phone = @Phone;", new { Phone = phone });
        Assert.Equal(32, storedCode!.Length);
    }

    [Theory]
    [InlineData("+919876543210")]
    [InlineData("+94112345678")]
    [InlineData("nonsense")]
    public async Task A_number_that_is_not_a_sri_lankan_mobile_is_400_invalid_phone(string phone)
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var response = await harness.PostAsync("/v1/auth/otp/request", new { phone, deviceId = "d" });

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "invalid-phone");
    }

    [Fact]
    public async Task A_missing_device_id_is_400_validation_failed()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var response = await harness.PostAsync("/v1/auth/otp/request", new { phone = IamHarness.NextPhone() });

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    [Fact]
    public async Task Reseller_is_neither_a_role_nor_a_capability()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        // AL-01. The portals do not use this endpoint either (AL-07).
        var response = await harness.PostAsync(
            "/v1/auth/otp/request", new { phone = IamHarness.NextPhone(), deviceId = "d", role = "reseller" });

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    [Fact]
    public async Task A_blocked_account_cannot_start_a_sign_in()
    {
        await using var harness = await IamHarness.StartWithoutResendCooldownAsync(postgres, redis);
        var phone = IamHarness.NextPhone();

        await harness.SignInAsync(phone, "device-blocked");

        var factory = harness.Services.GetRequiredService<INpgsqlConnectionFactory>();
        await using (var connection = await factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE iam.users SET is_blocked = true WHERE phone = @Phone;", new { Phone = phone });
        }

        var response = await harness.PostAsync("/v1/auth/otp/request", new { phone, deviceId = "device-blocked" });

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "user-blocked");
    }

    [Fact]
    public async Task Health_and_jwks_are_reachable_without_a_token_and_everything_else_is_not()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        Assert.Equal(HttpStatusCode.OK, (await harness.Client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.GetAsync("/.well-known/jwks.json")).StatusCode);

        // Deny-by-default (AL-06): logout is the one authenticated route in this slice.
        var response = await harness.Client.PostAsJsonAsync("/v1/auth/logout", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed record StoredSession(Guid UserId, string App, string DeviceKey, string Platform);
}
