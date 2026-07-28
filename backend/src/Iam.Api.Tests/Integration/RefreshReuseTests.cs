using System.Net;
using MageRide.Iam.Tests.Infrastructure;
using MageRide.Shared.Auth;
using MageRide.TestKit;

namespace MageRide.Iam.Tests.Integration;

/// <summary>
/// Refresh-token reuse detection across a whole rotation lineage (D-29).
/// </summary>
/// <remarks>
/// C020's <c>SessionLifecycleTests</c> proves one rotation and its immediate successor. This goes
/// further, because "revokes the session family" is only meaningful over a chain: a token stolen
/// early and replayed late has to end the session the thief's victim is holding *now*, not the
/// one that existed when it was taken.
/// </remarks>
[Collection(IamCollection.Name)]
public sealed class RefreshReuseTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task Replaying_the_first_token_of_a_chain_ends_the_whole_family()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var session = await harness.SignInAsync(IamHarness.NextPhone(), "device-chain");

        // Three honest rotations, as a handset does over an hour and a half.
        var first = session.RefreshToken;
        var second = await RotateAsync(harness, first);
        var third = await RotateAsync(harness, second);
        var fourth = await RotateAsync(harness, third);

        // The thief has a copy of the token from the start of the chain.
        await ProblemDocument.AssertAsync(
            await harness.PostAsync("/v1/auth/refresh", new { refreshToken = first }),
            HttpStatusCode.Unauthorized,
            "unauthorized");

        // Every link is now dead, including the one the real handset holds. That is the trade the
        // contract makes: we cannot tell the thief from the owner, so the lineage ends and the
        // owner signs in again.
        foreach (var token in new[] { second, third, fourth })
        {
            await ProblemDocument.AssertAsync(
                await harness.PostAsync("/v1/auth/refresh", new { refreshToken = token }),
                HttpStatusCode.Unauthorized,
                "unauthorized");
        }
    }

    /// <summary>
    /// The reuse rule is the same for a browser as for a handset, because it is the same session
    /// machinery (0107).
    /// </summary>
    [Fact]
    public async Task A_portal_session_rotates_and_detects_reuse_the_same_way()
    {
        const string password = "an-admin-password-12";

        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var email = IamSeed.NextEmail("admin");
        await harness.Seed.PortalUserAsync(email, MageRideRoles.Admin, password);

        var signedIn = await IamHarness.ReadJsonAsync(
            await harness.PostFromBrowserAsync("/v1/auth/password", new { email, password }));

        var first = signedIn.GetProperty("refreshToken").GetString()!;
        var second = await RotateAsync(harness, first);

        await ProblemDocument.AssertAsync(
            await harness.PostAsync("/v1/auth/refresh", new { refreshToken = first }),
            HttpStatusCode.Unauthorized,
            "unauthorized");

        await ProblemDocument.AssertAsync(
            await harness.PostAsync("/v1/auth/refresh", new { refreshToken = second }),
            HttpStatusCode.Unauthorized,
            "unauthorized");
    }

    /// <summary>
    /// A rotation re-reads the account, so a role granted mid-session (C029's driver grant)
    /// reaches the token within one refresh rather than at next sign-in.
    /// </summary>
    [Fact]
    public async Task A_rotation_picks_up_a_role_granted_since_the_sign_in()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var session = await harness.SignInAsync(IamHarness.NextPhone(), "device-grant");
        var userId = Guid.Parse(session.UserId);

        Assert.Equal(MageRideRoles.Passenger, RolesOf(session.AccessToken).Single());

        await harness.Seed.GrantRoleAsync(userId, MageRideRoles.Driver);

        var rotated = await IamHarness.ReadJsonAsync(
            await harness.PostAsync("/v1/auth/refresh", new { refreshToken = session.RefreshToken }));

        Assert.Equal(
            [MageRideRoles.Driver, MageRideRoles.Passenger],
            RolesOf(rotated.GetProperty("accessToken").GetString()!).Order(StringComparer.Ordinal));
    }

    private static async Task<string> RotateAsync(IamHarness harness, string refreshToken)
    {
        var response = await harness.PostAsync("/v1/auth/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await IamHarness.ReadJsonAsync(response)).GetProperty("refreshToken").GetString()!;
    }

    private static IEnumerable<string> RolesOf(string accessToken) =>
        new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(accessToken)
            .Claims
            .Where(claim => claim.Type == MageRideClaims.Role)
            .Select(claim => claim.Value);
}
