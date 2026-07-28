using System.Net;
using MageRide.Iam.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Iam.Tests.Integration;

/// <summary>
/// D3' §0 makes <c>Idempotency-Key</c> mandatory on every POST and the iam contract makes
/// <c>otp/verify</c> replay its token pair. Both run against <c>iam.command_log</c> (0104).
/// </summary>
[Collection<IamCollection>]
public sealed class IdempotencyTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task A_post_without_the_header_is_refused()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var response = await harness.PostWithoutKeyAsync(
            "/v1/auth/otp/request", new { phone = IamHarness.NextPhone(), deviceId = "d" });

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "idempotency-key-required");
    }

    [Fact]
    public async Task Replaying_a_verify_replays_the_issued_token_pair_rather_than_minting_a_second_session()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var phone = IamHarness.NextPhone();
        var (authId, code) = await harness.RequestOtpAsync(phone, "d");

        var key = Guid.NewGuid().ToString();
        var body = new { authId, otp = code, deviceId = "d" };

        var first = await harness.PostAsync("/v1/auth/otp/verify", body, key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var replay = await harness.PostAsync("/v1/auth/otp/verify", body, key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        // Byte for byte (R-14 / ADD §11.13) — a client that retried a timed-out request gets the
        // session it already has, not a second one that revoked the first.
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.True(replay.Headers.Contains("X-Idempotent-Replay"));
    }

    [Fact]
    public async Task The_same_key_with_a_different_body_is_a_conflict()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var key = Guid.NewGuid().ToString();

        var first = await harness.PostAsync(
            "/v1/auth/otp/request", new { phone = IamHarness.NextPhone(), deviceId = "d" }, key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await harness.PostAsync(
            "/v1/auth/otp/request", new { phone = IamHarness.NextPhone(), deviceId = "d" }, key);

        await ProblemDocument.AssertAsync(second, HttpStatusCode.Conflict, "idempotency-key-reuse");
    }

    [Fact]
    public async Task A_malformed_key_is_rejected_before_anything_is_minted()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var phone = IamHarness.NextPhone();

        var response = await harness.PostAsync("/v1/auth/otp/request", new { phone, deviceId = "d" }, "short");

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "idempotency-key-invalid");
        Assert.Equal(0, harness.Sms.CountFor(phone));
    }
}
