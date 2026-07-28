using System.Net;
using MageRide.Iam.Auth;
using MageRide.Iam.Tests.Infrastructure;
using MageRide.Shared.Auth;
using MageRide.Shared.Http;
using MageRide.TestKit;
using Microsoft.IdentityModel.JsonWebTokens;

namespace MageRide.Iam.Tests.Integration;

/// <summary>
/// The three portal sign-in methods AL-07 lists — password, Google, Apple — against a real
/// Postgres, and the fences that keep each one on its own surface.
/// </summary>
/// <remarks>
/// The OIDC tokens are minted by <see cref="TestOidcProvider"/> and verified by the real
/// <c>OidcTokenVerifier</c>: issuer, audience, expiry and signature are all checked by the code
/// that runs in production.
/// </remarks>
[Collection(IamCollection.Name)]
public sealed class PortalSignInTests(PostgresFixture postgres, RedisFixture redis)
{
    private const string Password = "an-admin-password-12";

    [Fact]
    public async Task An_admin_signs_in_with_a_password_and_gets_the_same_token_pair_an_app_gets()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var email = IamSeed.NextEmail("admin");
        var userId = await harness.Seed.PortalUserAsync(email, MageRideRoles.Admin, Password);

        var response = await harness.PostFromBrowserAsync("/v1/auth/password", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await IamHarness.ReadJsonAsync(response);

        // The same three fields, the same 1800, the same opaque refresh — one session shape for
        // every surface (D-29).
        Assert.Equal(1800, body.GetProperty("expiresIn").GetInt32());
        Assert.StartsWith("mr1.", body.GetProperty("refreshToken").GetString(), StringComparison.Ordinal);
        Assert.Equal(userId.ToString(), body.GetProperty("user").GetProperty("userId").GetString());
        Assert.Equal(email, body.GetProperty("user").GetProperty("email").GetString());

        var token = new JsonWebToken(body.GetProperty("accessToken").GetString()!);
        Assert.Equal(userId.ToString(), token.GetClaim(MageRideClaims.Subject).Value);
        Assert.Equal(MageRideRoles.Admin, token.GetClaim(MageRideClaims.Role).Value);
        Assert.Equal(MageRideApps.Admin, token.GetClaim(MageRideClaims.App).Value);

        // Session binding (AL-37): the browser is a device row like any handset.
        Assert.StartsWith(WebDeviceKeys.Prefix, token.GetClaim(MageRideClaims.DeviceId).Value, StringComparison.Ordinal);
        Assert.Equal(["web"], await harness.Seed.DevicePlatformsAsync(userId));
        Assert.Equal([MageRideApps.Admin], await harness.Seed.ActiveSessionAppsAsync(userId));
    }

    /// <summary>
    /// A fleet identity gets the <c>fleet_role</c>/<c>fleet_id</c> pair AL-03 scopes it by —
    /// and gets it on the same token every other surface issues.
    /// </summary>
    [Fact]
    public async Task A_fleet_owner_signs_in_to_the_fleet_portal_with_the_fleet_claims()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var email = IamSeed.NextEmail("fleet");
        var userId = await harness.Seed.PortalUserAsync(email, MageRideRoles.FleetOwner, Password);
        var fleetId = await harness.Seed.FleetMemberAsync(userId, FleetRoles.Owner);

        var response = await harness.PostFromBrowserAsync("/v1/auth/password", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await IamHarness.ReadJsonAsync(response);
        var token = new JsonWebToken(body.GetProperty("accessToken").GetString()!);

        Assert.Equal(MageRideApps.Fleet, token.GetClaim(MageRideClaims.App).Value);
        Assert.Equal(FleetRoles.Owner, token.GetClaim(MageRideClaims.FleetRole).Value);
        Assert.Equal(fleetId.ToString(), token.GetClaim(MageRideClaims.FleetId).Value);
        Assert.Equal(FleetRoles.Owner, body.GetProperty("user").GetProperty("fleetRole").GetString());
    }

    /// <summary>
    /// AL-08 is per surface, and 0107 makes the portals surfaces. So a person who is both an
    /// admin and a driver runs both at once, and neither ends the other.
    /// </summary>
    [Fact]
    public async Task A_portal_session_and_an_app_session_coexist()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var phone = IamHarness.NextPhone();
        var app = await harness.SignInAsync(phone, "handset-1", MageRideApps.Driver);
        var userId = Guid.Parse(app.UserId);

        // The same human, provisioned an internal role by a Super Admin (AL-06).
        var email = IamSeed.NextEmail("both");
        await harness.Seed.SetEmailAsync(userId, email, Password);
        await harness.Seed.GrantRoleAsync(userId, MageRideRoles.SupportCsr);

        var response = await harness.PostFromBrowserAsync("/v1/auth/password", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal([MageRideApps.Admin, MageRideApps.Driver], await harness.Seed.ActiveSessionAppsAsync(userId));
    }

    /// <summary>
    /// The other half of session binding: a second browser ends the first portal session, exactly
    /// as a second handset ends the first app session.
    /// </summary>
    [Fact]
    public async Task A_second_browser_ends_the_first_portal_session()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var email = IamSeed.NextEmail("admin");
        var userId = await harness.Seed.PortalUserAsync(email, MageRideRoles.Admin, Password);

        var first = await IamHarness.ReadJsonAsync(
            await harness.PostFromBrowserAsync("/v1/auth/password", new { email, password = Password }, "Firefox/141.0"));

        var second = await harness.PostFromBrowserAsync(
            "/v1/auth/password", new { email, password = Password }, "Chrome/141.0");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        // One live admin session, and the first browser's refresh token is spent.
        Assert.Single(await harness.Seed.ActiveSessionAppsAsync(userId));

        var rotate = await harness.PostAsync(
            "/v1/auth/refresh", new { refreshToken = first.GetProperty("refreshToken").GetString() });
        await ProblemDocument.AssertAsync(rotate, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task A_wrong_password_is_401_and_does_not_say_whether_the_account_exists()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var email = IamSeed.NextEmail("admin");
        await harness.Seed.PortalUserAsync(email, MageRideRoles.Admin, Password);

        var wrong = await harness.PostFromBrowserAsync("/v1/auth/password", new { email, password = "not-the-password" });
        var unknown = await harness.PostFromBrowserAsync(
            "/v1/auth/password", new { email = IamSeed.NextEmail("nobody"), password = Password });

        var wrongProblem = await ProblemDocument.AssertAsync(wrong, HttpStatusCode.Unauthorized, "unauthorized");
        var unknownProblem = await ProblemDocument.AssertAsync(unknown, HttpStatusCode.Unauthorized, "unauthorized");

        Assert.Equal(
            wrongProblem.Root.GetProperty("detail").GetString(),
            unknownProblem.Root.GetProperty("detail").GetString());
    }

    /// <summary>
    /// AL-07's central fence. A passenger or driver account has no portal to sign in to, whatever
    /// credential it presents.
    /// </summary>
    [Fact]
    public async Task An_app_account_with_a_password_still_cannot_use_a_portal()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var email = IamSeed.NextEmail("passenger");
        await harness.Seed.PortalUserAsync(email, MageRideRoles.Passenger, Password);

        var response = await harness.PostFromBrowserAsync("/v1/auth/password", new { email, password = Password });

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task A_blocked_portal_account_cannot_sign_in()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var email = IamSeed.NextEmail("admin");
        await harness.Seed.PortalUserAsync(email, MageRideRoles.Admin, Password, isBlocked: true);

        var response = await harness.PostFromBrowserAsync("/v1/auth/password", new { email, password = Password });

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "user-blocked");
    }

    /// <summary>
    /// The contract puts this at the gateway; it is repeated in the service because the fence
    /// matters more than the hop it is enforced at (AL-07).
    /// </summary>
    [Theory]
    [InlineData("/v1/auth/password")]
    [InlineData("/v1/auth/google")]
    [InlineData("/v1/auth/apple")]
    [InlineData("/v1/admin/auth/login")]
    public async Task A_request_from_an_app_is_refused_on_every_portal_route(string path)
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { email = "a@b.lk", password = Password }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add(MageRide.Shared.Http.MageRideHeaders.Platform, ClientPlatforms.Android);

        await ProblemDocument.AssertAsync(await harness.Client.SendAsync(request), HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Google_signs_an_admin_in_and_binds_the_provider_subject()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var email = IamSeed.NextEmail("admin");
        var userId = await harness.Seed.PortalUserAsync(email, MageRideRoles.Admin);

        var idToken = harness.Oidc.GoogleIdToken("google-subject-1", email);
        var response = await harness.PostFromBrowserAsync("/v1/auth/google", new { idToken });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await IamHarness.ReadJsonAsync(response);
        Assert.Equal(userId.ToString(), body.GetProperty("user").GetProperty("userId").GetString());
        Assert.Equal(("google", "google-subject-1"), await harness.Seed.FederatedIdentityAsync(userId));

        // Second sign-in resolves by subject, not by address — which is what makes it survive the
        // user changing their email at Google.
        await harness.Seed.SetEmailAsync(userId, IamSeed.NextEmail("renamed"), password: null);

        var again = await harness.PostFromBrowserAsync(
            "/v1/auth/google", new { idToken = harness.Oidc.GoogleIdToken("google-subject-1", email) });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    /// <summary>
    /// Internal roles are provisioned by a Super Admin (AL-06) and fleet users by their owner
    /// (AL-03). A verified Google identity with no MageRide account is not a first sign-in.
    /// </summary>
    [Fact]
    public async Task Google_does_not_create_an_account_that_was_never_provisioned()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var idToken = harness.Oidc.GoogleIdToken("google-subject-unknown", IamSeed.NextEmail("stranger"));

        var response = await harness.PostFromBrowserAsync("/v1/auth/google", new { idToken });

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "forbidden");
    }

    /// <summary>
    /// An unverified address is a string the provider let somebody type. Matching on it would let
    /// anyone who can get a token asserting an admin's address take the account.
    /// </summary>
    [Fact]
    public async Task An_unverified_provider_email_never_matches_an_existing_account()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var email = IamSeed.NextEmail("admin");
        await harness.Seed.PortalUserAsync(email, MageRideRoles.Admin);

        var idToken = harness.Oidc.GoogleIdToken("impostor", email, emailVerified: false);

        await ProblemDocument.AssertAsync(
            await harness.PostFromBrowserAsync("/v1/auth/google", new { idToken }),
            HttpStatusCode.Forbidden,
            "forbidden");
    }

    /// <summary>
    /// An ID token minted for a different OAuth client is a perfectly valid Google token. Without
    /// the audience check, any app on the internet could mint MageRide admin sessions.
    /// </summary>
    [Fact]
    public async Task A_token_for_another_oauth_client_is_refused()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var email = IamSeed.NextEmail("admin");
        await harness.Seed.PortalUserAsync(email, MageRideRoles.Admin);

        var idToken = harness.Oidc.GoogleIdToken("google-subject-2", email, audience: "someone-elses-client-id");

        await ProblemDocument.AssertAsync(
            await harness.PostFromBrowserAsync("/v1/auth/google", new { idToken }),
            HttpStatusCode.Unauthorized,
            "unauthorized");
    }

    [Fact]
    public async Task A_token_from_the_wrong_issuer_is_refused()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var email = IamSeed.NextEmail("admin");
        await harness.Seed.PortalUserAsync(email, MageRideRoles.Admin);

        var idToken = harness.Oidc.GoogleIdToken("google-subject-3", email, issuer: "https://accounts.evil.example");

        await ProblemDocument.AssertAsync(
            await harness.PostFromBrowserAsync("/v1/auth/google", new { idToken }),
            HttpStatusCode.Unauthorized,
            "unauthorized");
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var email = IamSeed.NextEmail("admin");
        await harness.Seed.PortalUserAsync(email, MageRideRoles.Admin);

        // Past the verifier's two-minute clock skew.
        var idToken = harness.Oidc.GoogleIdToken("google-subject-4", email, lifetime: TimeSpan.FromMinutes(-10));

        await ProblemDocument.AssertAsync(
            await harness.PostFromBrowserAsync("/v1/auth/google", new { idToken }),
            HttpStatusCode.Unauthorized,
            "unauthorized");
    }

    [Fact]
    public async Task A_token_signed_by_a_key_google_never_published_is_refused()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var email = IamSeed.NextEmail("admin");
        await harness.Seed.PortalUserAsync(email, MageRideRoles.Admin);

        await ProblemDocument.AssertAsync(
            await harness.PostFromBrowserAsync(
                "/v1/auth/google", new { idToken = TestOidcProvider.ForgedIdToken("google-subject-5", email) }),
            HttpStatusCode.Unauthorized,
            "unauthorized");
    }

    [Fact]
    public async Task Apple_signs_a_fleet_owner_in()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var email = IamSeed.NextEmail("fleet");
        var userId = await harness.Seed.PortalUserAsync(email, MageRideRoles.FleetOwner);
        await harness.Seed.FleetMemberAsync(userId, FleetRoles.Manager);

        var response = await harness.PostFromBrowserAsync(
            "/v1/auth/apple", new { idToken = harness.Oidc.AppleIdToken("apple-subject-1", email) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var token = new JsonWebToken(
            (await IamHarness.ReadJsonAsync(response)).GetProperty("accessToken").GetString()!);

        Assert.Equal(MageRideApps.Fleet, token.GetClaim(MageRideClaims.App).Value);
        Assert.Equal(FleetRoles.Manager, token.GetClaim(MageRideClaims.FleetRole).Value);
    }

    /// <summary>Apple is the one method AL-07 gives to a single surface.</summary>
    [Fact]
    public async Task Apple_is_not_a_way_into_the_admin_portal()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var email = IamSeed.NextEmail("admin");
        await harness.Seed.PortalUserAsync(email, MageRideRoles.Admin);

        await ProblemDocument.AssertAsync(
            await harness.PostFromBrowserAsync(
                "/v1/auth/apple", new { idToken = harness.Oidc.AppleIdToken("apple-subject-2", email) }),
            HttpStatusCode.Forbidden,
            "forbidden");
    }

    [Fact]
    public async Task The_admin_login_route_takes_a_password_and_refuses_a_non_internal_account()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var adminEmail = IamSeed.NextEmail("admin");
        await harness.Seed.PortalUserAsync(adminEmail, MageRideRoles.VerificationOfficer, Password);

        var fleetEmail = IamSeed.NextEmail("fleet");
        var fleetUser = await harness.Seed.PortalUserAsync(fleetEmail, MageRideRoles.FleetOwner, Password);
        await harness.Seed.FleetMemberAsync(fleetUser);

        var admin = await harness.PostFromBrowserAsync(
            "/v1/admin/auth/login", new { email = adminEmail, password = Password });
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);

        // The Fleet Portal's own credentials are valid — for the Fleet Portal (AL-02/AL-03).
        var fleet = await harness.PostFromBrowserAsync(
            "/v1/admin/auth/login", new { email = fleetEmail, password = Password });
        await ProblemDocument.AssertAsync(fleet, HttpStatusCode.Forbidden, "forbidden");
    }

    /// <summary>
    /// The contract's body is a <c>oneOf</c>. Picking an arm for a caller who sent both would
    /// silently ignore a credential they meant to send.
    /// </summary>
    [Fact]
    public async Task The_admin_login_body_must_carry_exactly_one_arm()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        await ProblemDocument.AssertAsync(
            await harness.PostFromBrowserAsync("/v1/admin/auth/login", new { }),
            HttpStatusCode.BadRequest,
            "validation-failed");

        await ProblemDocument.AssertAsync(
            await harness.PostFromBrowserAsync(
                "/v1/admin/auth/login", new { email = "a@b.lk", password = Password, googleAuthCode = "4/abc" }),
            HttpStatusCode.BadRequest,
            "validation-failed");
    }

    /// <summary>
    /// AL-37 removed the second factor. Neither arm of the admin login may answer with a
    /// challenge, and no route may exist to complete one.
    /// </summary>
    [Fact]
    public async Task There_is_no_mfa_step_and_no_mfa_route()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var email = IamSeed.NextEmail("admin");
        await harness.Seed.PortalUserAsync(email, MageRideRoles.SuperAdmin, Password);

        var response = await harness.PostFromBrowserAsync(
            "/v1/admin/auth/login", new { email, password = Password });

        var body = await IamHarness.ReadJsonAsync(response);

        // A token pair, not a challenge.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(body.TryGetProperty("mfaChallenge", out _));
        Assert.False(body.TryGetProperty("challengeId", out _));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("accessToken").GetString()));

        // The endpoints AL-37 removed stay removed. They answer 401 rather than 404 because the
        // kernel's fallback policy applies to a request with no endpoint at all — deny-by-default
        // covers the routes that do not exist as well as the ones that do (AL-06).
        foreach (var removed in new[] { "/v1/admin/auth/mfa/verify", "/v1/admin/auth/mfa/enrol", "/v1/auth/mfa/verify" })
        {
            var probe = await harness.PostAsync(removed, new { code = "123456" });

            Assert.False(probe.IsSuccessStatusCode, $"{removed} answered {(int)probe.StatusCode}");
            Assert.Equal(HttpStatusCode.Unauthorized, probe.StatusCode);
        }
    }
}
