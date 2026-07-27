using System.Net;
using System.Security.Claims;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Tests.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Shared.Tests.Auth;

/// <summary>Deny-by-default RBAC and the fleet sub-role hierarchy (AL-06, AL-03).</summary>
public sealed class AuthorizationTests
{
    private const string SchemeName = "Test";

    /// <summary>
    /// Turns an <c>X-Test-Claims</c> header of <c>name=value;name=value</c> into a principal, so
    /// the tests exercise the real policy evaluation without minting JWTs.
    /// </summary>
    private sealed class HeaderAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Claims", out var raw))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = raw.ToString()
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(pair => pair.Split('=', 2))
                .Select(parts => new Claim(parts[0], parts[1]))
                .ToArray();

            var identity = new ClaimsIdentity(claims, SchemeName, MageRideClaims.Subject, MageRideClaims.Role);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }

    private static WebApplication BuildApp()
    {
        var builder = TestHosts.CreateBuilder();

        builder.Services.AddProblemDetails(problem => problem.CustomizeProblemDetails =
            context => MageRideProblem.Enrich(context.HttpContext, context.ProblemDetails));
        builder.Services.AddAuthentication(SchemeName)
            .AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>(SchemeName, _ => { });
        builder.Services.AddMageRideAuthorization();

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();

        // Deliberately says nothing about authorization: the fallback policy must still protect it.
        app.MapGet("/silent", () => "ok");
        app.MapGet("/public", () => "ok").AllowAnonymous();
        app.MapGet("/admin-only", () => "ok").RequireMageRideRole(MageRideRoles.Admin);
        app.MapGet("/internal", () => "ok").RequireAuthorization(MageRidePolicies.InternalStaff);
        app.MapGet("/fleet-manager", () => "ok").RequireFleetRole(FleetRoles.Manager);

        return app;
    }

    private static async Task<HttpStatusCode> GetAsync(HttpClient client, string path, string? claims)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (claims is not null)
        {
            request.Headers.Add("X-Test-Claims", claims);
        }

        var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task An_endpoint_that_says_nothing_still_requires_authentication()
    {
        await using var app = BuildApp();
        await app.StartAsync();
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(client, "/silent", null));
        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, "/silent", $"{MageRideClaims.Subject}={Guid.NewGuid()}"));
    }

    [Fact]
    public async Task Allow_anonymous_opts_out_of_the_fallback()
    {
        await using var app = BuildApp();
        await app.StartAsync();
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, "/public", null));
    }

    [Fact]
    public async Task A_role_policy_admits_only_that_role()
    {
        await using var app = BuildApp();
        await app.StartAsync();
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, "/admin-only", $"{MageRideClaims.Role}=admin"));
        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(client, "/admin-only", $"{MageRideClaims.Role}=driver"));
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(client, "/admin-only", null));
    }

    /// <summary>AL-06: effective permissions are the union of <c>iam.user_roles</c>.</summary>
    [Fact]
    public async Task Several_role_claims_union_rather_than_conflict()
    {
        await using var app = BuildApp();
        await app.StartAsync();
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK,
            await GetAsync(client, "/admin-only", $"{MageRideClaims.Role}=driver;{MageRideClaims.Role}=admin"));
    }

    [Fact]
    public async Task The_internal_policy_admits_the_six_back_office_roles_only()
    {
        await using var app = BuildApp();
        await app.StartAsync();
        using var client = app.GetTestClient();

        foreach (var role in MageRideRoles.Internal)
        {
            Assert.Equal(HttpStatusCode.OK, await GetAsync(client, "/internal", $"{MageRideClaims.Role}={role}"));
        }

        foreach (var role in new[] { MageRideRoles.Passenger, MageRideRoles.Driver, MageRideRoles.FleetOwner })
        {
            Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(client, "/internal", $"{MageRideClaims.Role}={role}"));
        }
    }

    [Fact]
    public async Task Fleet_sub_roles_are_hierarchical()
    {
        await using var app = BuildApp();
        await app.StartAsync();
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, "/fleet-manager", $"{MageRideClaims.FleetRole}=owner"));
        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, "/fleet-manager", $"{MageRideClaims.FleetRole}=manager"));
        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(client, "/fleet-manager", $"{MageRideClaims.FleetRole}=viewer"));
        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(client, "/fleet-manager", $"{MageRideClaims.Subject}={Guid.NewGuid()}"));
    }

    [Fact]
    public void The_nine_canonical_roles_are_exactly_AL_06s_list()
    {
        Assert.Equal(9, MageRideRoles.All.Count);
        Assert.Equal(
            ["admin", "auditor", "driver", "finance_officer", "fleet_owner", "passenger", "super_admin", "support_csr", "verification_officer"],
            MageRideRoles.All.OrderBy(r => r, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void An_unknown_role_cannot_be_turned_into_a_policy()
    {
        Assert.Throws<ArgumentException>(() => MageRidePolicies.Role("reseller"));
        Assert.Throws<ArgumentException>(() => MageRidePolicies.FleetRole("administrator"));
    }

    [Fact]
    public void Fleet_role_ranking_is_owner_manager_viewer()
    {
        Assert.True(FleetRoles.Satisfies(FleetRoles.Owner, FleetRoles.Viewer));
        Assert.True(FleetRoles.Satisfies(FleetRoles.Manager, FleetRoles.Viewer));
        Assert.False(FleetRoles.Satisfies(FleetRoles.Viewer, FleetRoles.Manager));
        Assert.False(FleetRoles.Satisfies(null, FleetRoles.Viewer));
        Assert.False(FleetRoles.Satisfies("owner", "not-a-role"));
    }

    [Fact]
    public void Claims_accessors_read_the_D3_claim_names()
    {
        var subject = Guid.NewGuid();
        var fleet = Guid.NewGuid();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(MageRideClaims.Subject, subject.ToString()),
                new Claim(MageRideClaims.Role, MageRideRoles.Driver),
                new Claim(MageRideClaims.Role, "not-a-canonical-role"),
                new Claim(MageRideClaims.FleetRole, FleetRoles.Manager),
                new Claim(MageRideClaims.FleetId, fleet.ToString()),
                new Claim(MageRideClaims.DeviceId, "device-42"),
                new Claim(MageRideClaims.App, MageRideApps.Driver),
            ],
            SchemeName));

        Assert.Equal(subject, principal.SubjectId());
        Assert.Equal([MageRideRoles.Driver], principal.Roles());
        Assert.Equal(FleetRoles.Manager, principal.FleetRole());
        Assert.Equal(fleet, principal.FleetId());
        Assert.Equal("device-42", principal.DeviceId());
        Assert.Equal(MageRideApps.Driver, principal.App());
        Assert.Equal(MageRideRoles.Driver, principal.ActorType());
        Assert.True(principal.TryGetFleetScope(out var scope, out var fleetId));
        Assert.Equal(FleetRoles.Manager, scope);
        Assert.Equal(fleet, fleetId);
    }

    [Fact]
    public void An_anonymous_principal_records_as_anonymous_in_the_command_log()
    {
        Assert.Equal("anonymous", new ClaimsPrincipal(new ClaimsIdentity()).ActorType());
        Assert.Null(new ClaimsPrincipal(new ClaimsIdentity()).SubjectId());
    }
}
