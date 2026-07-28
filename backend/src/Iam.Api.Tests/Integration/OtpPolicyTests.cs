using System.Net;
using Dapper;
using MageRide.Iam.Tests.Infrastructure;
using MageRide.Shared.Persistence;
using MageRide.TestKit;
using Microsoft.Extensions.DependencyInjection;

namespace MageRide.Iam.Tests.Integration;

/// <summary>
/// C020's fourth definition-of-done item — D-32's two limits — plus the rest of the verify
/// contract: the attempt lock-out, the device binding and expiry.
/// </summary>
[Collection<IamCollection>]
public sealed class OtpPolicyTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task A_resend_inside_sixty_seconds_is_refused()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var phone = IamHarness.NextPhone();

        var first = await harness.PostAsync("/v1/auth/otp/request", new { phone, deviceId = "d" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var body = await IamHarness.ReadJsonAsync(first);
        Assert.Equal(60, body.GetProperty("cooldownSeconds").GetInt32());
        Assert.Equal(4, body.GetProperty("attemptsRemaining").GetInt32());
        Assert.False(body.GetProperty("isBlocked").GetBoolean());

        var second = await harness.PostAsync("/v1/auth/otp/request", new { phone, deviceId = "d" });
        var problem = await ProblemDocument.AssertAsync(second, HttpStatusCode.TooManyRequests, "otp-rate-limited");

        Assert.InRange(problem.GetInt32OrNull("retryAfterSeconds") ?? 0, 1, 60);
        Assert.Equal(1, harness.Sms.CountFor(phone));
    }

    [Fact]
    public async Task The_cooldown_and_the_hourly_cap_are_two_separate_limits()
    {
        // Cooldown off, so the five-per-hour budget is what is under test rather than the 60 s gap.
        await using var harness = await IamHarness.StartAsync(
            postgres, redis, new Dictionary<string, string?> { ["Otp:ResendCooldownSec"] = "0" });

        var phone = IamHarness.NextPhone();

        for (var i = 1; i <= 5; i++)
        {
            var allowed = await harness.PostAsync("/v1/auth/otp/request", new { phone, deviceId = "d" });
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

            var body = await IamHarness.ReadJsonAsync(allowed);
            Assert.Equal(5 - i, body.GetProperty("attemptsRemaining").GetInt32());
        }

        var sixth = await harness.PostAsync("/v1/auth/otp/request", new { phone, deviceId = "d" });

        await ProblemDocument.AssertAsync(sixth, HttpStatusCode.TooManyRequests, "otp-rate-limited");
        Assert.Equal(5, harness.Sms.CountFor(phone));
    }

    [Fact]
    public async Task A_resend_spends_the_same_budget_as_a_request()
    {
        await using var harness = await IamHarness.StartAsync(
            postgres, redis, new Dictionary<string, string?> { ["Otp:ResendCooldownSec"] = "0" });

        var phone = IamHarness.NextPhone();
        var (authId, firstCode) = await harness.RequestOtpAsync(phone, "d");

        var resend = await harness.PostAsync("/v1/auth/otp/resend", new { authId });
        Assert.Equal(HttpStatusCode.OK, resend.StatusCode);

        var body = await IamHarness.ReadJsonAsync(resend);
        Assert.Equal(3, body.GetProperty("attemptsRemaining").GetInt32());

        // A new code, and the old one is dead — the row's otp_hash was replaced.
        var secondCode = harness.Sms.LastCodeFor(phone);
        Assert.NotEqual(firstCode, secondCode);

        var stale = await harness.PostAsync("/v1/auth/otp/verify", new { authId, otp = firstCode, deviceId = "d" });
        await ProblemDocument.AssertAsync(stale, HttpStatusCode.Unauthorized, "invalid-otp");

        var fresh = await harness.PostAsync("/v1/auth/otp/verify", new { authId, otp = secondCode, deviceId = "d" });
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact]
    public async Task Resending_for_an_unknown_attempt_is_404()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var response = await harness.PostAsync("/v1/auth/otp/resend", new { authId = Guid.NewGuid().ToString() });

        await ProblemDocument.AssertAsync(response, HttpStatusCode.NotFound, "auth-not-found");
    }

    [Fact]
    public async Task A_wrong_code_is_401_and_the_fifth_one_locks_the_attempt()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var phone = IamHarness.NextPhone();
        var (authId, code) = await harness.RequestOtpAsync(phone, "d");

        var wrong = code == "000000" ? "111111" : "000000";

        for (var i = 0; i < 4; i++)
        {
            var response = await harness.PostAsync("/v1/auth/otp/verify", new { authId, otp = wrong, deviceId = "d" });
            await ProblemDocument.AssertAsync(response, HttpStatusCode.Unauthorized, "invalid-otp");
        }

        var fifth = await harness.PostAsync("/v1/auth/otp/verify", new { authId, otp = wrong, deviceId = "d" });
        await ProblemDocument.AssertAsync(fifth, HttpStatusCode.Locked, "otp-locked");

        // Locked means locked: the right code no longer helps.
        var correct = await harness.PostAsync("/v1/auth/otp/verify", new { authId, otp = code, deviceId = "d" });
        await ProblemDocument.AssertAsync(correct, HttpStatusCode.Locked, "otp-locked");
    }

    [Fact]
    public async Task A_wrong_entry_does_not_spoil_a_later_correct_one()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var phone = IamHarness.NextPhone();
        var (authId, code) = await harness.RequestOtpAsync(phone, "d");

        await harness.PostAsync("/v1/auth/otp/verify", new { authId, otp = "000001", deviceId = "d" });

        var response = await harness.PostAsync("/v1/auth/otp/verify", new { authId, otp = code, deviceId = "d" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_different_device_cannot_finish_someone_elses_sign_in()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var phone = IamHarness.NextPhone();
        var (authId, code) = await harness.RequestOtpAsync(phone, "the-real-handset");

        var response = await harness.PostAsync(
            "/v1/auth/otp/verify", new { authId, otp = code, deviceId = "somebody-elses-handset" });

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Conflict, "device-mismatch");

        // And it did not burn an attempt belonging to the device that asked for the code.
        var legitimate = await harness.PostAsync(
            "/v1/auth/otp/verify", new { authId, otp = code, deviceId = "the-real-handset" });
        Assert.Equal(HttpStatusCode.OK, legitimate.StatusCode);
    }

    [Fact]
    public async Task An_expired_code_is_400_otp_expired()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var phone = IamHarness.NextPhone();
        var (authId, code) = await harness.RequestOtpAsync(phone, "d");

        // Ageing the row beats sleeping through the real TTL: the clock the check uses is the
        // service's TimeProvider, and the harness runs the service out of process reach.
        var factory = harness.Services.GetRequiredService<INpgsqlConnectionFactory>();
        await using (var connection = await factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE iam.otp_attempts SET expires_at = now() - interval '1 second' WHERE auth_id = @AuthId;",
                new { AuthId = Guid.Parse(authId) });
        }

        var response = await harness.PostAsync("/v1/auth/otp/verify", new { authId, otp = code, deviceId = "d" });

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "otp-expired");
    }

    [Fact]
    public async Task An_unknown_auth_id_is_404_and_a_spent_one_is_too()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var phone = IamHarness.NextPhone();

        var unknown = await harness.PostAsync(
            "/v1/auth/otp/verify", new { authId = Guid.NewGuid().ToString(), otp = "123456", deviceId = "d" });
        await ProblemDocument.AssertAsync(unknown, HttpStatusCode.NotFound, "auth-not-found");

        var (authId, code) = await harness.RequestOtpAsync(phone, "d");
        Assert.Equal(HttpStatusCode.OK,
            (await harness.PostAsync("/v1/auth/otp/verify", new { authId, otp = code, deviceId = "d" })).StatusCode);

        // A fresh Idempotency-Key, so this is a genuine second execution rather than a replay.
        var spent = await harness.PostAsync("/v1/auth/otp/verify", new { authId, otp = code, deviceId = "d" });
        await ProblemDocument.AssertAsync(spent, HttpStatusCode.NotFound, "auth-not-found");
    }

    [Fact]
    public async Task A_malformed_auth_id_is_400_validation_failed()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var response = await harness.PostAsync(
            "/v1/auth/otp/verify", new { authId = "not-a-ulid", otp = "123456", deviceId = "d" });

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }
}
