using System.Net;
using MageRide.Registry.Tests.Infrastructure;
using MageRide.Shared.Auth;
using MageRide.TestKit;

namespace MageRide.Registry.Tests.Integration;

/// <summary>
/// Deny-by-default (AL-06) on every route this service maps.
/// </summary>
[Collection<PostgresCollection>]
public sealed class AuthorizationTests(PostgresFixture postgres)
{
    public static TheoryData<string, string> Routes => new()
    {
        { "POST", "/v1/vehicles" },
        { "GET", "/v1/vehicles/mine" },
        { "POST", "/v1/vehicles/00000000-0000-4000-8000-00000000c001/select-live" },
        { "POST", "/v1/dev/vehicles/00000000-0000-4000-8000-00000000c001/approve" },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Every_route_refuses_an_unauthenticated_caller(string method, string path)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var response = method == "GET"
            ? await harness.GetAsync(path, bearer: null)
            : await harness.PostAsync(path, null, bearer: null);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Every_route_refuses_a_passenger_who_opened_the_driver_app(string method, string path)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        // C020 decision 4: opening the Driver App does not grant the driver role, so this is a
        // real principal — app=driver, role=passenger — and not a contrived one. Holding `driver`
        // is what registry-svc onboarding grants (C029).
        var bearer = harness.Tokens.PassengerOnDriverApp(await harness.CreateDriverAsync());

        var response = method == "GET"
            ? await harness.GetAsync(path, bearer)
            : await harness.PostAsync(path, null, bearer);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task A_token_signed_by_a_key_this_service_does_not_trust_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        // A second issuer with its own key: exactly what a forged token looks like from here,
        // since registry-svc holds no signing key and trusts only iam-svc's published half.
        var forged = new TestTokenIssuer().Issue(Guid.NewGuid(), [MageRideRoles.Driver], MageRideApps.Driver);

        var response = await harness.GetAsync("/v1/vehicles/mine", forged);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }
}
