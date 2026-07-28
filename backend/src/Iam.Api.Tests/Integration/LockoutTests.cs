using System.Net;
using MageRide.Iam.Tests.Infrastructure;
using MageRide.Shared.Auth;
using MageRide.TestKit;
using Microsoft.Extensions.Time.Testing;

namespace MageRide.Iam.Tests.Integration;

/// <summary>
/// The failed-attempt lock-out AL-37 kept when it removed the MFA/TOTP step, and the optional IP
/// allow-list beside it.
/// </summary>
/// <remarks>
/// These are the compensating controls, so they are the difference between "MFA was removed" and
/// "MFA was removed and nothing replaced it". The counter is durable
/// (<c>iam.user_credentials</c>) rather than cached on purpose: a Redis flush must not hand an
/// attacker a clean slate on every internal account at once.
/// </remarks>
[Collection(IamCollection.Name)]
public sealed class LockoutTests(PostgresFixture postgres, RedisFixture redis)
{
    private const string Password = "an-admin-password-12";
    private const string Wrong = "not-the-password-at-all";

    [Fact]
    public async Task Three_wrong_passwords_lock_the_account_and_the_right_one_no_longer_works()
    {
        await using var harness = await IamHarness.StartAsync(
            postgres, redis, new Dictionary<string, string?> { ["Auth:MaxFailedAttempts"] = "3" });

        var email = IamSeed.NextEmail("admin");
        var userId = await harness.Seed.PortalUserAsync(email, MageRideRoles.Admin, Password);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var refused = await harness.PostFromBrowserAsync("/v1/auth/password", new { email, password = Wrong });
            await ProblemDocument.AssertAsync(refused, HttpStatusCode.Unauthorized, "unauthorized");
        }

        var third = await harness.PostFromBrowserAsync("/v1/auth/password", new { email, password = Wrong });
        var locked = await ProblemDocument.AssertAsync(third, HttpStatusCode.Locked, "otp-locked");
        Assert.True(locked.GetInt32OrNull("retryAfterSeconds") > 0);

        // The lock is checked before the verifier runs, so the correct password does not lift it.
        var correct = await harness.PostFromBrowserAsync("/v1/auth/password", new { email, password = Password });
        await ProblemDocument.AssertAsync(correct, HttpStatusCode.Locked, "otp-locked");

        var (failures, until) = await harness.Seed.CredentialStateAsync(userId);
        Assert.Equal(3, failures);
        Assert.NotNull(until);
    }

    [Fact]
    public async Task A_successful_sign_in_clears_the_counter()
    {
        await using var harness = await IamHarness.StartAsync(
            postgres, redis, new Dictionary<string, string?> { ["Auth:MaxFailedAttempts"] = "3" });

        var email = IamSeed.NextEmail("admin");
        var userId = await harness.Seed.PortalUserAsync(email, MageRideRoles.Admin, Password);

        await harness.PostFromBrowserAsync("/v1/auth/password", new { email, password = Wrong });
        await harness.PostFromBrowserAsync("/v1/auth/password", new { email, password = Wrong });
        Assert.Equal(2, (await harness.Seed.CredentialStateAsync(userId)).FailedAttempts);

        var response = await harness.PostFromBrowserAsync("/v1/auth/password", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Consecutive failures, not lifetime ones — otherwise every long-lived account eventually
        // locks itself out on typos.
        Assert.Equal(0, (await harness.Seed.CredentialStateAsync(userId)).FailedAttempts);
    }

    /// <summary>
    /// It is a lock-out, not a ban. The shortest legal duration is 30 seconds, so this drives the
    /// whole graph off a fake clock rather than sleeping.
    /// </summary>
    [Fact]
    public async Task The_lock_expires_on_its_own()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);

        await using var harness = await IamHarness.StartAsync(
            postgres,
            redis,
            new Dictionary<string, string?>
            {
                ["Auth:MaxFailedAttempts"] = "1",
                ["Auth:LockoutDuration"] = "00:05:00",
            },
            clock);

        var email = IamSeed.NextEmail("admin");
        await harness.Seed.PortalUserAsync(email, MageRideRoles.Admin, Password);

        await ProblemDocument.AssertAsync(
            await harness.PostFromBrowserAsync("/v1/auth/password", new { email, password = Wrong }),
            HttpStatusCode.Locked,
            "otp-locked");

        clock.Advance(TimeSpan.FromMinutes(4));
        await ProblemDocument.AssertAsync(
            await harness.PostFromBrowserAsync("/v1/auth/password", new { email, password = Password }),
            HttpStatusCode.Locked,
            "otp-locked");

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(
            HttpStatusCode.OK,
            (await harness.PostFromBrowserAsync("/v1/auth/password", new { email, password = Password })).StatusCode);
    }

    /// <summary>
    /// Guessing at an address that does not exist must not be distinguishable from guessing at one
    /// that does — including by whether the second attempt starts answering 423.
    /// </summary>
    [Fact]
    public async Task Guessing_at_an_unknown_address_never_locks_anything()
    {
        await using var harness = await IamHarness.StartAsync(
            postgres, redis, new Dictionary<string, string?> { ["Auth:MaxFailedAttempts"] = "2" });

        var email = IamSeed.NextEmail("nobody");

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var response = await harness.PostFromBrowserAsync("/v1/auth/password", new { email, password = Wrong });
            await ProblemDocument.AssertAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
        }
    }

    /// <summary>
    /// The lock counts wrong <em>passwords</em>. Locking Google sign-in from failed password
    /// guesses would hand an attacker a denial of service against an admin who never uses one.
    /// </summary>
    [Fact]
    public async Task A_locked_password_does_not_lock_google_sign_in()
    {
        await using var harness = await IamHarness.StartAsync(
            postgres, redis, new Dictionary<string, string?> { ["Auth:MaxFailedAttempts"] = "1" });

        var email = IamSeed.NextEmail("admin");
        await harness.Seed.PortalUserAsync(email, MageRideRoles.Admin, Password);

        await ProblemDocument.AssertAsync(
            await harness.PostFromBrowserAsync("/v1/auth/password", new { email, password = Wrong }),
            HttpStatusCode.Locked,
            "otp-locked");

        var google = await harness.PostFromBrowserAsync(
            "/v1/auth/google", new { idToken = harness.Oidc.GoogleIdToken("google-locked-1", email) });

        Assert.Equal(HttpStatusCode.OK, google.StatusCode);
    }

    [Fact]
    public async Task An_internal_role_outside_the_allow_list_is_refused()
    {
        await using var harness = await IamHarness.StartAsync(
            postgres,
            redis,
            new Dictionary<string, string?> { ["Auth:InternalRoleIpAllowList:0"] = "10.20.0.0/16" });

        var email = IamSeed.NextEmail("admin");
        await harness.Seed.PortalUserAsync(email, MageRideRoles.Admin, Password);

        var outside = await harness.PostFromBrowserAsync(
            "/v1/auth/password", new { email, password = Password }, forwardedFor: "203.0.113.7");
        await ProblemDocument.AssertAsync(outside, HttpStatusCode.Forbidden, "forbidden");

        var inside = await harness.PostFromBrowserAsync(
            "/v1/auth/password", new { email, password = Password }, forwardedFor: "10.20.4.9");
        Assert.Equal(HttpStatusCode.OK, inside.StatusCode);
    }

    /// <summary>The list is scoped to internal roles — a fleet owner signs in from anywhere.</summary>
    [Fact]
    public async Task The_allow_list_does_not_apply_to_a_fleet_account()
    {
        await using var harness = await IamHarness.StartAsync(
            postgres,
            redis,
            new Dictionary<string, string?> { ["Auth:InternalRoleIpAllowList:0"] = "10.20.0.0/16" });

        var email = IamSeed.NextEmail("fleet");
        var userId = await harness.Seed.PortalUserAsync(email, MageRideRoles.FleetOwner, Password);
        await harness.Seed.FleetMemberAsync(userId);

        var response = await harness.PostFromBrowserAsync(
            "/v1/auth/password", new { email, password = Password }, forwardedFor: "203.0.113.7");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
