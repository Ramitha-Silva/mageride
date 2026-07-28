using System.Net;
using MageRide.Iam.Rbac;
using MageRide.Iam.Tests.Infrastructure;
using MageRide.Shared.Auth;
using MageRide.TestKit;

namespace MageRide.Iam.Tests.Integration;

/// <summary>
/// DoD: "the RBAC matrix test covers every (role, privileged endpoint) pair in URD §2.3 with an
/// explicit allow/deny" — the endpoint half. <c>PermissionMatrixTests</c> is the (area, role) half.
/// </summary>
/// <remarks>
/// <para>
/// iam-svc's privileged surface is <c>/v1/admin/rbac/**</c>, gated on URD §2.3's "User &amp; role
/// management (RBAC)" row. Every one of the nine canonical roles is driven against every one of
/// the five endpoints and the expected answer is <b>derived from the matrix</b> rather than
/// written out here, so the fence and the spec cannot drift apart. All 45 pairs are asserted.
/// </para>
/// <para>
/// The tokens are real sessions, obtained the way each role obtains one: phone OTP for the two app
/// roles (AL-07) and a provisioned portal account with a password for the fleet owner and the six
/// internal roles (AL-06, AL-03) — no portal sign-in creates an account.
/// </para>
/// </remarks>
[Collection<IamCollection>]
public sealed class RbacEndpointTests(PostgresFixture postgres, RedisFixture redis)
{
    private const string Password = "correct-horse-battery-staple";

    /// <summary>The five privileged routes and the capability each declares.</summary>
    private static readonly (string Method, string Path, PermissionGrant Needed)[] PrivilegedEndpoints =
    [
        ("GET", "/v1/admin/rbac/matrix", PermissionGrant.Read),
        ("GET", "/v1/admin/rbac/roles", PermissionGrant.Read),
        ("GET", "/v1/admin/rbac/users/{userId}", PermissionGrant.Read),
        ("POST", "/v1/admin/rbac/users/{userId}/roles", PermissionGrant.Write),
        ("DELETE", "/v1/admin/rbac/users/{userId}/roles/auditor", PermissionGrant.Write),
    ];

    [Fact]
    public async Task Every_role_against_every_privileged_endpoint_is_an_explicit_allow_or_deny()
    {
        await using var harness = await IamHarness.StartWithoutResendCooldownAsync(postgres, redis);

        // The account every request acts on. It holds `auditor` as a grant so the DELETE arm has
        // something real to revoke, and its primary role is passenger so revoking never trips the
        // primary-role guard.
        var subject = await harness.SignInAsync(IamHarness.NextPhone(), "subject-handset");
        var subjectId = Guid.Parse(subject.UserId);

        var failures = new List<string>();
        var pairs = 0;

        foreach (var role in MageRideRoles.All.Order(StringComparer.Ordinal))
        {
            var token = await TokenForAsync(harness, role);

            foreach (var (method, template, needed) in PrivilegedEndpoints)
            {
                // Re-granted before each attempt so the DELETE arm always has a row to remove and
                // an allowed caller cannot be refused for a reason other than authorization.
                await harness.Seed.GrantRoleAsync(subjectId, MageRideRoles.Auditor);

                var path = template.Replace("{userId}", subjectId.ToString(), StringComparison.Ordinal);
                var response = await SendAsync(harness, method, path, token);

                var expectedAllowed = PermissionMatrix.Cell(FeatureAreas.RoleManagement, role).Satisfies(needed);
                var actuallyAllowed = response.StatusCode != HttpStatusCode.Forbidden;

                if (expectedAllowed != actuallyAllowed)
                {
                    failures.Add(
                        $"{role,-22} {method,-6} {template} -> {(int)response.StatusCode}; URD §2.3 says " +
                        $"{(expectedAllowed ? "allow" : "deny")} " +
                        $"({PermissionMatrix.Cell(FeatureAreas.RoleManagement, role).Symbol} on role-management)");
                }

                if (expectedAllowed)
                {
                    // An allowed caller must actually succeed, not merely avoid a 403.
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                }
                else
                {
                    await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "forbidden");
                }

                pairs++;
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Assert.Equal(MageRideRoles.All.Count * PrivilegedEndpoints.Length, pairs);
    }

    /// <summary>
    /// The most surprising cell in URD §2.3, called out by name: an Administrator is ➖ on RBAC.
    /// URD §2.4 — "Admin — … **No** RBAC/role management."
    /// </summary>
    [Fact]
    public async Task An_administrator_is_refused_role_management()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var token = await TokenForAsync(harness, MageRideRoles.Admin);
        var subject = await harness.Seed.PassengerAsync(IamHarness.NextPhone());

        await ProblemDocument.AssertAsync(
            await harness.GetAsync($"/v1/admin/rbac/users/{subject}", token), HttpStatusCode.Forbidden, "forbidden");

        await ProblemDocument.AssertAsync(
            await harness.PostAsync($"/v1/admin/rbac/users/{subject}/roles", new { role = "driver" }, bearer: token),
            HttpStatusCode.Forbidden,
            "forbidden");
    }

    /// <summary>An Auditor reads and never writes — URD §2.4, "no write access anywhere".</summary>
    [Fact]
    public async Task An_auditor_may_read_the_grants_and_not_change_them()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var token = await TokenForAsync(harness, MageRideRoles.Auditor);
        var subject = await harness.Seed.PassengerAsync(IamHarness.NextPhone());

        Assert.Equal(
            HttpStatusCode.OK, (await harness.GetAsync($"/v1/admin/rbac/users/{subject}", token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await harness.GetAsync("/v1/admin/rbac/matrix", token)).StatusCode);

        await ProblemDocument.AssertAsync(
            await harness.PostAsync($"/v1/admin/rbac/users/{subject}/roles", new { role = "driver" }, bearer: token),
            HttpStatusCode.Forbidden,
            "forbidden");
    }

    [Fact]
    public async Task A_super_admin_grants_a_role_and_the_grant_records_who_made_it()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var email = IamSeed.NextEmail("super");
        var actorId = await harness.Seed.PortalUserAsync(email, MageRideRoles.SuperAdmin, Password);
        var token = await harness.PortalTokenAsync(email, Password);

        var subject = await harness.Seed.PassengerAsync(IamHarness.NextPhone());

        var response = await harness.PostAsync(
            $"/v1/admin/rbac/users/{subject}/roles", new { role = MageRideRoles.SupportCsr }, bearer: token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await IamHarness.ReadJsonAsync(response);
        Assert.Contains(
            MageRideRoles.SupportCsr,
            body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));

        // AL-06 wants provenance on an internal grant, and iam.user_roles.granted_by is where it lives.
        Assert.Equal(actorId, await harness.Seed.RoleGrantedByAsync(subject, MageRideRoles.SupportCsr));
    }

    [Fact]
    public async Task Re_granting_a_role_is_idempotent_and_does_not_rewrite_its_provenance()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var firstEmail = IamSeed.NextEmail("super-one");
        var firstActor = await harness.Seed.PortalUserAsync(firstEmail, MageRideRoles.SuperAdmin, Password);
        var firstToken = await harness.PortalTokenAsync(firstEmail, Password);

        var secondEmail = IamSeed.NextEmail("super-two");
        await harness.Seed.PortalUserAsync(secondEmail, MageRideRoles.SuperAdmin, Password);
        var secondToken = await harness.PortalTokenAsync(secondEmail, Password);

        var subject = await harness.Seed.PassengerAsync(IamHarness.NextPhone());

        await harness.PostAsync(
            $"/v1/admin/rbac/users/{subject}/roles", new { role = MageRideRoles.Auditor }, bearer: firstToken);
        var repeat = await harness.PostAsync(
            $"/v1/admin/rbac/users/{subject}/roles", new { role = MageRideRoles.Auditor }, bearer: secondToken);

        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);

        // granted_by is the only record of who let this account in; a retry is not a new decision.
        Assert.Equal(firstActor, await harness.Seed.RoleGrantedByAsync(subject, MageRideRoles.Auditor));
    }

    [Fact]
    public async Task A_role_that_is_not_one_of_the_nine_is_refused()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var token = await TokenForAsync(harness, MageRideRoles.SuperAdmin);
        var subject = await harness.Seed.PassengerAsync(IamHarness.NextPhone());

        // AL-01: "reseller" is neither a role nor a capability.
        await ProblemDocument.AssertAsync(
            await harness.PostAsync($"/v1/admin/rbac/users/{subject}/roles", new { role = "reseller" }, bearer: token),
            HttpStatusCode.BadRequest,
            "validation-failed");
    }

    /// <summary>
    /// <c>IUserRepository.RolesAsync</c> unions the grants with <c>iam.users.role</c>, so deleting
    /// the grant row for a primary role changes nothing any evaluator can see. Answering 200 would
    /// show the console a role that every service still honours.
    /// </summary>
    [Fact]
    public async Task The_primary_role_cannot_be_revoked_as_a_grant()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var token = await TokenForAsync(harness, MageRideRoles.SuperAdmin);
        var subject = await harness.SignInAsync(IamHarness.NextPhone(), "handset", "driver");

        var response = await harness.DeleteAsync(
            $"/v1/admin/rbac/users/{subject.UserId}/roles/driver", token);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Conflict, "conflict");
    }

    /// <summary>
    /// AL-06 makes Super Admin the only principal who can grant <c>super_admin</c>. Revoking your
    /// own is not losing an account — it is losing the ability to give it back.
    /// </summary>
    [Fact]
    public async Task A_super_admin_cannot_revoke_their_own_super_admin_role()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var email = IamSeed.NextEmail("super");
        var actorId = await harness.Seed.PortalUserAsync(email, MageRideRoles.Admin, Password);
        await harness.Seed.GrantRoleAsync(actorId, MageRideRoles.SuperAdmin);
        var token = await harness.PortalTokenAsync(email, Password);

        var response = await harness.DeleteAsync(
            $"/v1/admin/rbac/users/{actorId}/roles/{MageRideRoles.SuperAdmin}", token);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Conflict, "conflict");

        // Another Super Admin can, though — the rule is about self-revocation, not about the role.
        var otherEmail = IamSeed.NextEmail("super-two");
        await harness.Seed.PortalUserAsync(otherEmail, MageRideRoles.SuperAdmin, Password);
        var otherToken = await harness.PortalTokenAsync(otherEmail, Password);

        var byAnother = await harness.DeleteAsync(
            $"/v1/admin/rbac/users/{actorId}/roles/{MageRideRoles.SuperAdmin}", otherToken);

        Assert.Equal(HttpStatusCode.OK, byAnother.StatusCode);
    }

    [Fact]
    public async Task Revoking_a_role_nobody_holds_is_not_found()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var token = await TokenForAsync(harness, MageRideRoles.SuperAdmin);
        var subject = await harness.Seed.PassengerAsync(IamHarness.NextPhone());

        await ProblemDocument.AssertAsync(
            await harness.DeleteAsync($"/v1/admin/rbac/users/{subject}/roles/{MageRideRoles.Auditor}", token),
            HttpStatusCode.NotFound,
            "not-found");
    }

    [Fact]
    public async Task An_account_that_does_not_exist_is_not_found()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var token = await TokenForAsync(harness, MageRideRoles.SuperAdmin);

        await ProblemDocument.AssertAsync(
            await harness.GetAsync($"/v1/admin/rbac/users/{Guid.NewGuid()}", token),
            HttpStatusCode.NotFound,
            "not-found");
    }

    [Fact]
    public async Task The_matrix_endpoint_serves_the_whole_of_URD_2_3()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var token = await TokenForAsync(harness, MageRideRoles.SuperAdmin);

        var body = await IamHarness.ReadJsonAsync(await harness.GetAsync("/v1/admin/rbac/matrix", token));

        Assert.Equal(MageRideRoles.All.Count, body.GetProperty("roles").GetArrayLength());

        var areas = body.GetProperty("areas").EnumerateArray().ToArray();
        Assert.Equal(FeatureAreas.All.Count, areas.Length);

        foreach (var area in areas)
        {
            var cells = area.GetProperty("cells");

            foreach (var role in MageRideRoles.All)
            {
                // Deny-by-default means "explicitly ➖", not "absent".
                Assert.True(cells.TryGetProperty(role, out var cell), $"{area.GetProperty("featureArea")} has no {role} cell.");
                Assert.False(string.IsNullOrWhiteSpace(cell.GetProperty("symbol").GetString()));
            }
        }
    }

    [Fact]
    public async Task The_role_catalog_lists_the_nine_and_marks_the_six_internal_ones()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var token = await TokenForAsync(harness, MageRideRoles.SuperAdmin);

        var items = (await IamHarness.ReadJsonAsync(await harness.GetAsync("/v1/admin/rbac/roles", token)))
            .GetProperty("items")
            .EnumerateArray()
            .ToArray();

        Assert.Equal(MageRideRoles.All.Count, items.Length);
        Assert.Equal(
            MageRideRoles.Internal.Count,
            items.Count(item => item.GetProperty("isInternal").GetBoolean()));
    }

    /// <summary>
    /// URD §2.2: "the UI is rendered from the same permission model the API enforces server-side".
    /// Every authenticated caller may read their own grants — that is not a privileged act.
    /// </summary>
    [Fact]
    public async Task Any_caller_may_read_their_own_effective_permissions()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var session = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        var body = await IamHarness.ReadJsonAsync(await harness.GetAsync("/v1/me/permissions", session.AccessToken));

        Assert.Equal(session.UserId, body.GetProperty("userId").GetString());
        Assert.Equal(["passenger"], body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));

        var entries = body.GetProperty("permissions").EnumerateArray().ToArray();
        Assert.Equal(FeatureAreas.All.Count, entries.Length);

        var passenger = entries.Single(e => e.GetProperty("featureArea").GetString() == FeatureAreas.Passenger.Key);
        Assert.Equal(["read", "write"], passenger.GetProperty("grants").EnumerateArray().Select(g => g.GetString()));
        Assert.Equal("✅", passenger.GetProperty("symbol").GetString());

        // ➖ areas are present and empty, never absent.
        var rbac = entries.Single(e => e.GetProperty("featureArea").GetString() == FeatureAreas.RoleManagement.Key);
        Assert.Equal(0, rbac.GetProperty("grants").GetArrayLength());
        Assert.Equal("➖", rbac.GetProperty("symbol").GetString());
    }

    /// <summary>
    /// A grant made through this API reaches the account's own token at its next refresh, not
    /// instantly — C026's rotation re-reads the principal.
    /// </summary>
    [Fact]
    public async Task A_new_grant_reaches_the_holder_within_one_refresh()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var token = await TokenForAsync(harness, MageRideRoles.SuperAdmin);
        var subject = await harness.SignInAsync(IamHarness.NextPhone(), "handset");

        await harness.PostAsync(
            $"/v1/admin/rbac/users/{subject.UserId}/roles", new { role = MageRideRoles.Driver }, bearer: token);

        var rotated = await harness.PostAsync("/v1/auth/refresh", new { refreshToken = subject.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);

        var refreshedToken = (await IamHarness.ReadJsonAsync(rotated)).GetProperty("accessToken").GetString()!;
        var permissions = await IamHarness.ReadJsonAsync(await harness.GetAsync("/v1/me/permissions", refreshedToken));

        Assert.Contains(
            MageRideRoles.Driver, permissions.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
    }

    /// <summary>
    /// A token for one of the nine roles, obtained the way that role obtains one: phone OTP for
    /// the two app surfaces (AL-07), a provisioned portal account for the other seven.
    /// </summary>
    private static async Task<string> TokenForAsync(IamHarness harness, string role)
    {
        if (role is MageRideRoles.Passenger or MageRideRoles.Driver)
        {
            return (await harness.SignInAsync(IamHarness.NextPhone(), $"handset-{role}", role)).AccessToken;
        }

        var email = IamSeed.NextEmail(role);
        await harness.Seed.PortalUserAsync(email, role, Password);
        return await harness.PortalTokenAsync(email, Password);
    }

    private static Task<HttpResponseMessage> SendAsync(IamHarness harness, string method, string path, string token) =>
        method switch
        {
            "GET" => harness.GetAsync(path, token),
            "POST" => harness.PostAsync(path, new { role = MageRideRoles.Auditor }, bearer: token),
            "DELETE" => harness.DeleteAsync(path, token),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unsupported method."),
        };
}
