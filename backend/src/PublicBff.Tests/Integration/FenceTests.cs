using MageRide.PublicBff.Endpoints;
using MageRide.PublicBff.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.PublicBff.Tests.Integration;

/// <summary>
/// The four fences, asserted against the running route table rather than read out of the source.
/// </summary>
[Collection<PublicBffCollection>]
public sealed class FenceTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task No_route_asks_for_authentication_and_every_route_is_under_public_track()
    {
        await using var harness = await PublicBffHarness.StartAsync(postgres, redis);

        var routes = harness.Routes()
            .Where(static route => !route.Route.StartsWith("/health", StringComparison.Ordinal)
                                   && !route.Route.StartsWith("/metrics", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(routes);

        foreach (var (route, allowsAnonymous, inPublicGroup) in routes)
        {
            Assert.StartsWith(PublicTrackEndpoints.Prefix, route, StringComparison.Ordinal);

            // AL-44: the share token is the whole credential and no SCR-WT page has a bearer to
            // present. A route that asked for one would be a route nobody could reach.
            Assert.True(allowsAnonymous, $"{route} requires authorization.");

            // Outside the group means it has not been through the token gate.
            Assert.True(inPublicGroup, $"{route} was mapped outside the public-track group.");
        }
    }

    [Fact]
    public async Task There_is_no_call_route_and_no_endpoint_returns_a_masked_number()
    {
        await using var harness = await PublicBffHarness.StartAsync(postgres, redis);

        // AL-48 removed the ride-scoped proxy-DID lease, the CPaaS bridge and the confirm-your-number
        // step in full. Several pre-AL-48 spec lines still describe them, so the absence is asserted
        // rather than assumed.
        Assert.DoesNotContain(
            harness.Routes(), route => route.Route.Contains("/call", StringComparison.OrdinalIgnoreCase));

        var ride = await harness.Seed.RideAsync(state: "InProgress", kind: 2);
        var token = await harness.Seed.TokenAsync(
            ride.RideId, "package_recipient", harness.Now.AddHours(4));

        var body = await PublicBffHarness.OkAsync(
            await harness.GetAsync($"/public/track/{token}"), "the package snapshot");

        // The number the page dials is the driver's real MSISDN (US-26.3). A masked one would be a
        // number belonging to a DID pool that does not exist.
        var phone = body.GetProperty("driver").GetProperty("phone").GetString();

        Assert.Equal(ride.DriverPhone, phone);
        Assert.DoesNotContain('*', phone!);
        Assert.DoesNotContain('X', phone!);
    }

    [Fact]
    public void The_service_refuses_to_start_with_a_route_outside_the_surface()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        var app = builder.Build();

        // A route somebody added because it was convenient — authenticated, outside the prefix, and
        // outside the group. Any one of the three is a refusal.
        app.MapGet("/v1/admin/track/{token}", () => Results.Ok()).RequireAuthorization();

        var failure = Assert.Throws<InvalidOperationException>(() => PublicBffApplication.GuardTheSurface(app));

        Assert.Contains("public-bff refuses to start", failure.Message, StringComparison.Ordinal);
        Assert.Contains("/public/track", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_service_refuses_to_start_if_the_call_route_is_ever_re_added()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        var app = builder.Build();

        app.MapGroup(PublicTrackEndpoints.Prefix)
            .AllowAnonymous()
            .WithMetadata(new PublicSurfaceMarker())
            .MapPost("/{token}/call", () => Results.Ok());

        var failure = Assert.Throws<InvalidOperationException>(() => PublicBffApplication.GuardTheSurface(app));

        Assert.Contains("AL-48", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_route_of_the_family_is_present()
    {
        await using var harness = await PublicBffHarness.StartAsync(postgres, redis);

        var routes = harness.Routes().Select(static route => route.Route).ToArray();

        // The six operations `public-bff.yaml` declares, and no seventh.
        foreach (var expected in new[]
                 {
                     "/public/track/{token}",
                     "/public/track/{token}/live",
                     "/public/track/{token}/pickup/confirm",
                     "/public/track/{token}/pickup/decline",
                     "/public/track/{token}/sos",
                     "/public/track/{token}/receipt",
                 })
        {
            Assert.Contains(expected, routes);
        }

        var surface = routes
            .Where(static route => route.StartsWith(PublicTrackEndpoints.Prefix, StringComparison.Ordinal))
            .Distinct()
            .ToArray();

        Assert.Equal(6, surface.Length);
    }

    [Fact]
    public async Task Nothing_on_this_surface_writes_a_ride_row()
    {
        await using var harness = await PublicBffHarness.StartAsync(postgres, redis);

        var ride = await harness.Seed.RideAsync(state: "InProgress", kind: 2);
        var token = await harness.Seed.TokenAsync(
            ride.RideId, "package_recipient", harness.Now.AddHours(4));

        long before;
        long after;

        await using (var connection = await harness.OpenAsync())
        {
            before = await Dapper.SqlMapper.ExecuteScalarAsync<long>(
                connection, "SELECT xmin::text::bigint FROM rides.rides WHERE id = @Id;", new { Id = ride.RideId });
        }

        (await harness.GetAsync($"/public/track/{token}")).Dispose();
        (await harness.GetAsync($"/public/track/{token}/live?since=")).Dispose();

        await using (var connection = await harness.OpenAsync())
        {
            after = await Dapper.SqlMapper.ExecuteScalarAsync<long>(
                connection, "SELECT xmin::text::bigint FROM rides.rides WHERE id = @Id;", new { Id = ride.RideId });
        }

        // The only rows this service writes are the share token's own meter and burn. `xmin` moving
        // would mean something here updated a ride, which is ride-svc's alone (R-01).
        Assert.Equal(before, after);
    }
}
