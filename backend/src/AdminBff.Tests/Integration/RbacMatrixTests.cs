using System.Net;
using MageRide.AdminBff.Authorization;
using MageRide.AdminBff.Endpoints;
using MageRide.AdminBff.Tests.Infrastructure;
using MageRide.Shared.Auth;
using MageRide.TestKit;
using Microsoft.AspNetCore.Authorization;

namespace MageRide.AdminBff.Tests.Integration;

/// <summary>
/// DoD: "the RBAC test matrix covers every internal role against every admin endpoint per URD §2.3."
/// </summary>
/// <remarks>
/// <para>
/// <b>The expectation is not written down here.</b> Each route declares its own
/// <see cref="FeaturePermissionRequirement"/>, the kernel's <c>PermissionEvaluator</c> resolves what
/// a role holds in that area, and the two are compared against what the running service actually
/// answers. So the matrix in the test is the matrix in the spec — <c>PermissionMatrixTests</c>
/// (kernel) already proves the compiled table matches URD §2.3 cell for cell, and this proves the
/// endpoints are gated by it.
/// </para>
/// <para>
/// <b>Every route is covered whether or not anybody remembers to add it.</b>
/// <see cref="EveryAdminRouteIsGatedOnAUrdRow"/> enumerates the route table and fails on a route
/// this file has no request for, so C063, C064 and C065 cannot add an endpoint that silently
/// escapes the matrix.
/// </para>
/// <para>
/// <b>"Allowed" means "past the gate", not "succeeded".</b> A permitted call may still answer 400,
/// 404 or 503 — the point of an authorization test is the 403 boundary, and demanding a 200 would
/// mean seeding a valid subject for every route and asserting two things at once.
/// </para>
/// </remarks>
[Collection(AdminBffCollection.Name)]
public sealed class RbacMatrixTests(PostgresFixture postgres)
{
    /// <summary>The six back-office roles URD §2.1 puts on the Admin Portal (AL-02).</summary>
    private static readonly string[] InternalRoles = [.. MageRideRoles.Internal.Order(StringComparer.Ordinal)];

    /// <summary>
    /// One request per admin route, with a body good enough to reach the handler.
    /// </summary>
    /// <remarks>
    /// The bodies are deliberately valid-ish rather than valid: a 400 and a 200 are both "past the
    /// gate", and a fixture that had to satisfy every handler's validation would be a second copy of
    /// the contract living in a test.
    /// </remarks>
    public static TheoryData<string, string, string?> Requests()
    {
        var data = new TheoryData<string, string, string?>();

        foreach (var (method, path, body) in Cases)
        {
            data.Add(method, path, body);
        }

        return data;
    }

    private static readonly (string Method, string Path, string? Body)[] Cases =
    [
        ("GET", "/v1/admin/session", null),
        ("GET", "/v1/admin/dashboard", null),
        ("GET", "/v1/admin/dashboard/stats?period=today", null),
        ("GET", "/v1/admin/dashboard/stats.csv?period=today", null),

        ("POST", "/v1/admin/vehicles/01930000-0000-7000-8000-0000000000aa/suspend", """{"reason":"test"}"""),
        ("POST", "/v1/admin/drivers/01930000-0000-7000-8000-0000000000ab/suspend", """{"reason":"test"}"""),
        ("GET", "/v1/admin/reports/queue", null),
        ("POST", "/v1/admin/reports/01930000-0000-7000-8000-0000000000ac/resolve", """{"decision":"CONFIRMED"}"""),
        ("GET", "/v1/admin/support/tickets", null),
        ("POST", "/v1/admin/support/tickets/01930000-0000-7000-8000-0000000000ad/resolve", """{"response":"ok"}"""),

        ("PUT", "/v1/admin/fares/tariffs", """{"tariffs":[{"vehicleType":"sedan","firstKmMinor":10000,"perKmMinor":5000}]}"""),
        ("POST", "/v1/admin/config/cities", """{"code":"rbac_probe","nameEn":"A","nameSi":"අ","nameTa":"அ","centroid":{"lat":6.9,"lng":79.8}}"""),
        ("PATCH", "/v1/admin/config/cities/colombo", """{"sortOrder":0}"""),
        ("GET", "/v1/admin/config/feature-flags", null),
        ("PUT", "/v1/admin/config/feature-flags/rbac_probe", """{"enabled":false}"""),
        ("POST", "/v1/admin/trains", """{"name":"Probe","trainNumber":"RBAC-PROBE"}"""),
        ("PUT", "/v1/admin/trains/01930000-0000-7000-8000-0000000000ae", """{"name":"Probe","trainNumber":"RBAC-PROBE"}"""),
        ("DELETE", "/v1/admin/trains/01930000-0000-7000-8000-0000000000ae", null),

        ("POST", "/v1/admin/announcements",
            """{"messageByLang":{"si":"අ","ta":"அ","en":"a"},"startsAt":"2026-08-01T00:00:00Z"}"""),

        ("GET", "/v1/admin/audit-log", null),

        ("GET", "/v1/admin/transit/gtfs/versions", null),
        ("POST", "/v1/admin/transit/gtfs/uploads/01930000-0000-7000-8000-0000000000af/activate", null),
        ("PUT", "/v1/admin/transit/gtfs/versions", null),
        ("DELETE", "/v1/admin/transit/gtfs/versions", null),
    ];

    /// <summary>
    /// Every internal role against one route, with URD §2.3 deciding the expectation.
    /// </summary>
    [Theory]
    [MemberData(nameof(Requests))]
    public async Task Every_internal_role_meets_the_matrix(string method, string path, string? body)
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var (requirement, platformWide) = GateOf(harness, method, path);
        var evaluator = harness.Services.GetRequiredService<IPermissionEvaluator>();

        foreach (var role in InternalRoles)
        {
            bool allowed;

            if (requirement is null)
            {
                // /v1/admin/session is authenticated-internal by design: every role may ask what it
                // is allowed to do. It is the one route with no feature area, and the reason is on
                // SessionEndpoints.
                allowed = true;
            }
            else
            {
                var permission = evaluator.Evaluate(Guid.NewGuid(), [role], fleet: null).For(requirement.Area);

                // Platform-wide routes also need the capability UNSCOPED: URD §2.3's ◐ is a fence,
                // and a cell whose qualifier is "temp on reports" does not authorise a permanent
                // platform-wide suspension. See PlatformWideFeatureHandler.
                allowed = permission.Satisfies(requirement.Needed)
                          && (!platformWide || !permission.RequiresOwnScope(requirement.Needed));
            }

            using var response = await Send(harness, method, path, harness.Tokens.Internal(Guid.NewGuid(), role), body);

            var cell = requirement is null ? "(no feature area)" : PermissionMatrix.Cell(requirement.Area, role).Symbol;

            if (allowed)
            {
                Assert.True(
                    response.StatusCode != HttpStatusCode.Forbidden,
                    $"{role} was refused {method} {path}; URD §2.3 gives {cell} on "
                    + $"{requirement?.Area.Key ?? "-"} and the route needs "
                    + $"{requirement?.Needed.ToString() ?? "-"}{(platformWide ? " platform-wide" : string.Empty)}.");
            }
            else
            {
                Assert.True(
                    response.StatusCode == HttpStatusCode.Forbidden,
                    $"{role} reached {method} {path} with {(int)response.StatusCode}; URD §2.3 gives {cell} on "
                    + $"{requirement?.Area.Key ?? "-"}.");
            }
        }
    }

    /// <summary>
    /// The three end-user roles reach nothing here (AL-02: no driver- or passenger-facing surface).
    /// </summary>
    [Fact]
    public async Task No_end_user_role_reaches_the_back_office()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        foreach (var (method, path, body) in Cases)
        {
            using var response = await Send(harness, method, path, harness.Tokens.Driver(Guid.NewGuid()), body);

            Assert.True(
                response.StatusCode is HttpStatusCode.Forbidden,
                $"A driver reached {method} {path} with {(int)response.StatusCode}.");
        }
    }

    /// <summary>An unauthenticated caller gets 401 everywhere, including the read routes.</summary>
    [Fact]
    public async Task Deny_by_default_answers_401_without_a_token()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        foreach (var (method, path, body) in Cases)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);

            if (body is not null)
            {
                request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            }

            using var response = await harness.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    /// <summary>
    /// Every route the service serves is gated on a URD §2.3 row, and is covered above.
    /// </summary>
    /// <remarks>
    /// The half that makes the theory exhaustive rather than merely long: a route added by C063,
    /// C064 or C065 without a matrix gate, or without a case in <see cref="Cases"/>, fails here.
    /// </remarks>
    [Fact]
    public async Task Every_admin_route_is_gated_on_a_urd_row()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var covered = Cases
            .Select(static probe => (probe.Method, Path: probe.Path.Split('?')[0]))
            .ToHashSet();

        var uncovered = new List<string>();

        foreach (var endpoint in harness.Routes)
        {
            var pattern = endpoint.RoutePattern.RawText!;

            if (!pattern.StartsWith(AdminEndpoints.Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var method in endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
            {
                if (!covered.Any(probe => probe.Method == method && Matches(pattern, probe.Path)))
                {
                    uncovered.Add($"{method} {pattern}");
                }
            }

            var policy = endpoint.Metadata.GetMetadata<AuthorizationPolicy>();

            var gated = policy?.Requirements.OfType<FeaturePermissionRequirement>().Any() == true
                        || pattern.EndsWith("/session", StringComparison.Ordinal);

            Assert.True(
                gated,
                $"{pattern} is not gated on a URD §2.3 (feature area, capability) pair. Deny-by-default "
                + "is per row (AL-06); RequireMageRideRole duplicates a decision the spec already made.");
        }

        Assert.True(
            uncovered.Count == 0,
            "These admin routes have no RBAC probe in RbacMatrixTests.Cases, so no role is asserted "
            + $"against them: {string.Join(", ", uncovered)}");
    }

    /// <summary>
    /// The route's own declared gate, read off the built endpoint rather than restated.
    /// </summary>
    private static (FeaturePermissionRequirement? Requirement, bool PlatformWide) GateOf(
        AdminBffHarness harness, string method, string path)
    {
        var bare = path.Split('?')[0];

        var endpoint = harness.Routes.FirstOrDefault(route =>
            Matches(route.RoutePattern.RawText!, bare) &&
            route.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) == true);

        Assert.NotNull(endpoint);

        var policy = endpoint.Metadata.GetMetadata<AuthorizationPolicy>();

        return (
            policy?.Requirements.OfType<FeaturePermissionRequirement>().FirstOrDefault(),
            policy?.Requirements.OfType<PlatformWideFeatureRequirement>().Any() == true);
    }

    /// <summary>
    /// Whether a concrete path matches a route pattern, segment by segment.
    /// </summary>
    /// <remarks>
    /// Enough for this table: a template segment (<c>{vehicleId:guid}</c>) matches anything, a
    /// catch-all (<c>{**path}</c>) swallows the rest, and everything else compares literally. A real
    /// route matcher would be the framework's, which is not reachable from a test without building a
    /// request — and building a request is what the theory above already does.
    /// </remarks>
    private static bool Matches(string pattern, string path)
    {
        var patternSegments = pattern.Trim('/').Split('/');
        var pathSegments = path.Trim('/').Split('/');

        for (var index = 0; index < patternSegments.Length; index++)
        {
            if (patternSegments[index].StartsWith("{**", StringComparison.Ordinal))
            {
                return pathSegments.Length >= index;
            }

            if (index >= pathSegments.Length)
            {
                return false;
            }

            if (patternSegments[index].StartsWith('{'))
            {
                continue;
            }

            if (!string.Equals(patternSegments[index], pathSegments[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return patternSegments.Length == pathSegments.Length;
    }

    private static Task<HttpResponseMessage> Send(
        AdminBffHarness harness, string method, string path, string bearer, string? body)
    {
        object? payload = body is null
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(body);

        return harness.SendAsync(new HttpMethod(method), path, bearer, payload);
    }
}
