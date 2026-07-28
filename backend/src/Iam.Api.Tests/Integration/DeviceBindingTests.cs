using System.Net;
using MageRide.Iam.Tests.Infrastructure;
using MageRide.Shared.Auth;
using MageRide.Shared.Http;
using MageRide.TestKit;

namespace MageRide.Iam.Tests.Integration;

/// <summary>
/// Device binding: the <c>iam.devices</c> row a session hangs off, and the push token that
/// travels with it (AL-08, D-30, 0107).
/// </summary>
[Collection(IamCollection.Name)]
public sealed class DeviceBindingTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>
    /// C020 accepted <c>fcmToken</c> on otp/request and dropped it, because its home —
    /// <c>iam.devices.fcm_apns_token</c> — is on a row that does not exist until verify identifies
    /// the user. 0107 parks it on the attempt in between.
    /// </summary>
    [Fact]
    public async Task The_push_token_sent_at_request_time_reaches_the_device_row()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var phone = IamHarness.NextPhone();
        const string device = "handset-with-push";
        const string fcm = "fcm-registration-token-abc";

        var requested = await harness.PostAsync(
            "/v1/auth/otp/request", new { phone, deviceId = device, fcmToken = fcm, role = MageRideApps.Driver });
        Assert.Equal(HttpStatusCode.OK, requested.StatusCode);

        var authId = (await IamHarness.ReadJsonAsync(requested)).GetProperty("authId").GetString();
        var verified = await harness.PostAsync(
            "/v1/auth/otp/verify", new { authId, otp = harness.Sms.LastCodeFor(phone), deviceId = device });
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);

        var userId = Guid.Parse(
            (await IamHarness.ReadJsonAsync(verified)).GetProperty("user").GetProperty("userId").GetString()!);

        Assert.Equal(fcm, await harness.Seed.DeviceFcmTokenAsync(userId, device));
    }

    /// <summary>
    /// An oversized or empty token is dropped rather than rejected: the contract caps it at 512
    /// and marks it optional, and the worst case is one device with no push until it registers
    /// again — not a user who cannot sign in.
    /// </summary>
    [Fact]
    public async Task An_oversized_push_token_is_dropped_rather_than_failing_the_sign_in()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var phone = IamHarness.NextPhone();
        const string device = "handset-bad-push";

        var requested = await harness.PostAsync(
            "/v1/auth/otp/request", new { phone, deviceId = device, fcmToken = new string('x', 600) });
        Assert.Equal(HttpStatusCode.OK, requested.StatusCode);

        var authId = (await IamHarness.ReadJsonAsync(requested)).GetProperty("authId").GetString();
        var verified = await harness.PostAsync(
            "/v1/auth/otp/verify", new { authId, otp = harness.Sms.LastCodeFor(phone), deviceId = device });

        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);

        var userId = Guid.Parse(
            (await IamHarness.ReadJsonAsync(verified)).GetProperty("user").GetProperty("userId").GetString()!);

        Assert.Null(await harness.Seed.DeviceFcmTokenAsync(userId, device));
    }

    /// <summary>
    /// One row per install (0105's partial unique index), so a re-sign-in on the same handset does
    /// not accumulate device rows — and the previously registered push token survives a sign-in
    /// that did not carry one.
    /// </summary>
    [Fact]
    public async Task Signing_in_again_on_the_same_handset_reuses_its_device_row()
    {
        await using var harness = await IamHarness.StartWithoutResendCooldownAsync(postgres, redis);

        var phone = IamHarness.NextPhone();
        const string device = "handset-reused";

        var first = await harness.PostAsync(
            "/v1/auth/otp/request", new { phone, deviceId = device, fcmToken = "fcm-first" });
        var authId = (await IamHarness.ReadJsonAsync(first)).GetProperty("authId").GetString();
        var signedIn = await harness.PostAsync(
            "/v1/auth/otp/verify", new { authId, otp = harness.Sms.LastCodeFor(phone), deviceId = device });

        var userId = Guid.Parse(
            (await IamHarness.ReadJsonAsync(signedIn)).GetProperty("user").GetProperty("userId").GetString()!);

        await harness.SignInAsync(phone, device);

        Assert.Equal(["android"], await harness.Seed.DevicePlatformsAsync(userId));
        Assert.Equal("fcm-first", await harness.Seed.DeviceFcmTokenAsync(userId, device));
    }

    /// <summary>
    /// <c>iam.devices.platform</c> now admits three values, and each surface writes its own:
    /// the gateway's <c>X-Platform</c> for the apps (D-31) and <c>web</c> for a browser (0107).
    /// </summary>
    [Fact]
    public async Task Each_surface_records_its_own_platform()
    {
        const string password = "an-admin-password-12";

        await using var harness = await IamHarness.StartWithoutResendCooldownAsync(postgres, redis);

        var phone = IamHarness.NextPhone();
        var (authId, code) = await harness.RequestOtpAsync(phone, "iphone-1");

        // The device row is written by verify, so that is the call whose X-Platform decides what
        // iam.devices.platform says (D-31).
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/otp/verify")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { authId, otp = code, deviceId = "iphone-1" }),
        };
        request.Headers.Add(MageRideHeaders.IdempotencyKey, Guid.NewGuid().ToString());
        request.Headers.Add(MageRideHeaders.Platform, ClientPlatforms.Ios);

        var verified = await harness.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);

        var userId = Guid.Parse(
            (await IamHarness.ReadJsonAsync(verified)).GetProperty("user").GetProperty("userId").GetString()!);

        // Same person, now also signing in to a portal from a browser.
        var email = IamSeed.NextEmail("staff");
        await harness.Seed.SetEmailAsync(userId, email, password);
        await harness.Seed.GrantRoleAsync(userId, MageRideRoles.SupportCsr);

        Assert.Equal(
            HttpStatusCode.OK,
            (await harness.PostFromBrowserAsync("/v1/auth/password", new { email, password })).StatusCode);

        Assert.Equal(["ios", "web"], await harness.Seed.DevicePlatformsAsync(userId));
    }

    /// <summary>
    /// Presenting the wrong device is not a failed guess, so it must not spend the attempt budget
    /// of the device that actually asked for the code (C020, re-asserted here because 0107 moved
    /// the columns the check reads).
    /// </summary>
    [Fact]
    public async Task Verifying_from_a_different_device_is_a_conflict_not_a_wrong_code()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var phone = IamHarness.NextPhone();
        var (authId, code) = await harness.RequestOtpAsync(phone, "handset-a");

        var wrongDevice = await harness.PostAsync(
            "/v1/auth/otp/verify", new { authId, otp = code, deviceId = "handset-b" });
        await ProblemDocument.AssertAsync(wrongDevice, HttpStatusCode.Conflict, "device-mismatch");

        var rightDevice = await harness.PostAsync(
            "/v1/auth/otp/verify", new { authId, otp = code, deviceId = "handset-a" });
        Assert.Equal(HttpStatusCode.OK, rightDevice.StatusCode);
    }
}
