using System.Net;
using MageRide.ApiGateway.Tests.Infrastructure;

namespace MageRide.ApiGateway.Tests;

/// <summary>
/// Per-route edge ceilings (D6' §8.2). The bucket is keyed by route and caller, so exhausting one
/// route must not close another.
/// </summary>
public sealed class RateLimitTests
{
    /// <summary>Three tokens with an hour's refill, so the fourth call in a test is deterministic.</summary>
    private static Dictionary<string, string?> ThreePerHour(string policy) => new()
    {
        [$"Gateway:RateLimits:Policies:{policy}:Capacity"] = "3",
        [$"Gateway:RateLimits:Policies:{policy}:RefillTokens"] = "3",
        [$"Gateway:RateLimits:Policies:{policy}:RefillPeriod"] = "01:00:00",
    };

    [Fact]
    public async Task Exhausting_a_policy_yields_429_rate_limited_with_retry_after()
    {
        await using var gateway = await GatewayHarness.StartAsync(ThreePerHour("default"));

        for (var i = 1; i <= 3; i++)
        {
            using var allowed = await gateway.Client.GetAsync("/v1/users/me");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using var refused = await gateway.Client.GetAsync("/v1/users/me");

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.NotNull(refused.Headers.RetryAfter);
        Assert.True(refused.Headers.RetryAfter!.Delta > TimeSpan.Zero);

        var problem = await ProblemDocument.ReadAsync(refused);
        Assert.Equal("rate-limited", problem.Code);
    }

    [Fact]
    public async Task Exhausting_one_route_does_not_close_another()
    {
        await using var gateway = await GatewayHarness.StartAsync(ThreePerHour("default"));

        for (var i = 1; i <= 4; i++)
        {
            using var _ = await gateway.Client.GetAsync("/v1/users/me");
        }

        // Same 'default' policy, different route: its own bucket. A driver who has burned through
        // profile reads must still be able to reach an unrelated surface.
        using var other = await gateway.Client.GetAsync("/v1/support/faq");
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task A_route_on_a_different_policy_is_unaffected()
    {
        await using var gateway = await GatewayHarness.StartAsync(ThreePerHour("default"));

        for (var i = 1; i <= 4; i++)
        {
            using var _ = await gateway.Client.GetAsync("/v1/users/me");
        }

        // /v1/sos is on the 'sos' policy, which was not narrowed. D-33 budgets a 5 s SOS p99; an
        // edge ceiling shared with ordinary reads would be the wrong thing to run out of.
        using var sos = await gateway.Client.GetAsync("/v1/sos/01JZ/history");
        Assert.Equal(HttpStatusCode.OK, sos.StatusCode);
    }

    [Fact]
    public async Task Disabling_the_limiter_removes_every_ceiling()
    {
        var settings = ThreePerHour("default");
        settings["Gateway:RateLimits:Enabled"] = "false";

        await using var gateway = await GatewayHarness.StartAsync(settings);

        for (var i = 1; i <= 10; i++)
        {
            using var response = await gateway.Client.GetAsync("/v1/users/me");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}
