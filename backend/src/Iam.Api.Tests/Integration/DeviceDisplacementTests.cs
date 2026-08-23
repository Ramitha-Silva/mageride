using System.Net;
using MageRide.Iam.Tests.Infrastructure;
using MageRide.Shared.Auth;
using MageRide.TestKit;

namespace MageRide.Iam.Tests.Integration;

/// <summary>
/// AL-08's displacement: signing in on a new handset ends the old one's session (Δ MCS-30).
/// </summary>
/// <remarks>
/// <para>
/// The revocation itself predates this component and worked — <c>IssueAsync</c> revokes every
/// active session for the user and app in the same transaction that opens the new one. What did
/// not work is the part a driver notices: nothing outside iam-svc read session state, so the
/// displaced device's <em>access</em> token stayed valid until it expired, up to thirty minutes,
/// and <c>403 device-revoked</c> — which both apps have handled since C014 and which
/// <c>mobile_db_schema.md</c> §0.4 makes one of the three events that wipe the local database —
/// had no producer anywhere in the backend.
/// </para>
/// <para>
/// These tests pin the behaviour in both directions. The fail-open case matters as much as the
/// refusal: it is the one somebody will later "fix" into a fail-closed check and take the whole
/// platform out with a Redis restart.
/// </para>
/// <para>
/// Every test here signs in TWICE on one phone, which is what the OTP resend cooldown exists to
/// refuse — so they take the harness that opts out of it, exactly as its own remarks invite. The
/// hourly cap stays on; it is the per-minute cooldown that would otherwise make every one of these
/// a test of the rate limiter.
/// </para>
/// </remarks>
[Collection(IamCollection.Name)]
public sealed class DeviceDisplacementTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>
    /// The headline: the old handset is refused on its very next request, not at its next refresh.
    /// </summary>
    [Fact]
    public async Task A_sign_in_on_another_device_refuses_the_first_devices_next_request()
    {
        await using var harness = await IamHarness.StartWithoutResendCooldownAsync(postgres, redis);

        var phone = IamHarness.NextPhone();

        var first = await harness.SignInAsync(phone, "old-handset", MageRideApps.Driver);

        // The old token works, so the refusal below is displacement and not a bad token.
        Assert.Equal(HttpStatusCode.OK, (await harness.GetAsync("/v1/users/me", first.AccessToken)).StatusCode);

        await harness.SignInAsync(phone, "new-handset", MageRideApps.Driver);

        var displaced = await harness.GetAsync("/v1/users/me", first.AccessToken);

        Assert.Equal(HttpStatusCode.Forbidden, displaced.StatusCode);

        // The code is the whole point: the apps wipe the local database on `device-revoked` and
        // route to Login, and treat a plain 403 as an ordinary refusal to be shown as copy.
        var problem = await IamHarness.ReadJsonAsync(displaced);
        Assert.Equal("device-revoked", problem.GetProperty("code").GetString());
    }

    /// <summary>The new handset is unaffected — displacement is one-directional.</summary>
    [Fact]
    public async Task The_device_that_displaced_the_other_keeps_working()
    {
        await using var harness = await IamHarness.StartWithoutResendCooldownAsync(postgres, redis);

        var phone = IamHarness.NextPhone();

        await harness.SignInAsync(phone, "old-handset", MageRideApps.Driver);
        var second = await harness.SignInAsync(phone, "new-handset", MageRideApps.Driver);

        Assert.Equal(HttpStatusCode.OK, (await harness.GetAsync("/v1/users/me", second.AccessToken)).StatusCode);
    }

    /// <summary>
    /// US-1.12: one person may hold a driver session and a passenger session at once.
    /// </summary>
    /// <remarks>
    /// The revocation is scoped to <c>(user, app)</c> for exactly this reason, and a tombstone
    /// written per session id inherits that scoping. A driver signing in on their own phone must
    /// not sign themselves out of the passenger app on the same phone.
    /// </remarks>
    [Fact]
    public async Task Signing_into_the_driver_app_does_not_displace_the_same_persons_passenger_session()
    {
        await using var harness = await IamHarness.StartWithoutResendCooldownAsync(postgres, redis);

        var phone = IamHarness.NextPhone();

        var passenger = await harness.SignInAsync(phone, "one-handset", MageRideApps.Passenger);
        await harness.SignInAsync(phone, "one-handset", MageRideApps.Driver);

        Assert.Equal(HttpStatusCode.OK, (await harness.GetAsync("/v1/users/me", passenger.AccessToken)).StatusCode);
    }

    /// <summary>
    /// **The fail-open direction, and the reason it is written down.**
    /// </summary>
    /// <remarks>
    /// A tombstone that is present refuses; one that is absent means nothing at all. Redis is
    /// best-effort across this platform — the mirror beside the tombstone says "Postgres remains
    /// authoritative" in its own writer — so a check that read absence as revocation would sign
    /// every driver on the platform out of a Redis restart.
    /// </remarks>
    [Fact]
    public async Task A_missing_tombstone_is_not_evidence_and_leaves_the_session_alone()
    {
        await using var harness = await IamHarness.StartWithoutResendCooldownAsync(postgres, redis);

        var phone = IamHarness.NextPhone();

        var first = await harness.SignInAsync(phone, "old-handset", MageRideApps.Driver);
        await harness.SignInAsync(phone, "new-handset", MageRideApps.Driver);

        Assert.Equal(HttpStatusCode.Forbidden, (await harness.GetAsync("/v1/users/me", first.AccessToken)).StatusCode);

        // Exactly what a Redis eviction or a restart looks like from the kernel's side.
        await harness.ForgetRevocationAsync(first.AccessToken);

        var afterLoss = await harness.GetAsync("/v1/users/me", first.AccessToken);

        Assert.Equal(HttpStatusCode.OK, afterLoss.StatusCode);
    }

    /// <summary>
    /// The displaced device cannot refresh its way back either, which is the older half of the
    /// rule and stays true.
    /// </summary>
    [Fact]
    public async Task The_displaced_device_cannot_refresh()
    {
        await using var harness = await IamHarness.StartWithoutResendCooldownAsync(postgres, redis);

        var phone = IamHarness.NextPhone();

        var first = await harness.SignInAsync(phone, "old-handset", MageRideApps.Driver);
        await harness.SignInAsync(phone, "new-handset", MageRideApps.Driver);

        var refreshed = await harness.PostAsync("/v1/auth/refresh", new { refreshToken = first.RefreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, refreshed.StatusCode);
    }
}
