using MageRide.Fanout.Realtime;
using MageRide.HotPath.Tests.Infrastructure;
using MageRide.Shared.Geo;
using MageRide.TestKit;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Contract = MageRide.Shared.Realtime.LiveHub;

namespace MageRide.HotPath.Tests.Integration;

/// <summary>
/// DoD: "the passenger joins exactly the 19 cells of res-7 + ring(2)."
/// </summary>
/// <remarks>
/// Every connection here is a real SignalR client over a real WebSocket against a real Kestrel, so
/// the <c>access_token</c> query-string authentication, the JSON hub protocol and the group
/// bookkeeping are all the ones the apps meet. A hub method invoked in-process would exercise none
/// of them.
/// </remarks>
[Collection<HotPathCollection>]
public sealed class LiveHubTests(EmqxFixture emqx, RedpandaFixture redpanda, RedisFixture redis)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>The DoD assertion, made through the hub rather than against the arithmetic alone.</summary>
    [Fact]
    public async Task A_passenger_joins_exactly_the_19_cells_of_res_7_plus_ring_2()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await StartAsync();

        var cells = GeoCells.ViewCells(Samples.ColomboFort);

        // R-06. The wrong figure — "res-8 + ring(1) ≈ 3 km" — is still in circulation and would put
        // 7 cells here covering about a third of the area.
        Assert.Equal(19, cells.Count);
        Assert.Equal(GeoCells.PassengerViewCellCount, cells.Count);

        await using var connection = harness.PassengerConnection();
        await connection.StartAsync();

        await connection.InvokeAsync(Contract.Methods.JoinGeocells, cells.ToArray());

        var subscriptions = harness.FanoutServices.GetRequiredService<ICellSubscriptions>();

        Assert.Equal(19, subscriptions.CellsOf(ConnectionIdOf(subscriptions, cells[0])).Count);
        Assert.Equal(19, cells.Count(cell => subscriptions.ActiveCells.Contains(cell)));

        await connection.StopAsync();
    }

    [Fact]
    public async Task The_hub_refuses_a_connection_with_no_access_token()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await StartAsync();

        // Deny-by-default (AL-06): the kernel's fallback policy covers the hub endpoint too, so a
        // hub that forgot [Authorize] would still demand a bearer. Both are in place; this proves
        // the effect rather than the mechanism.
        await using var connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(new Uri(harness.FanoutBaseAddress), Contract.Path),
                options => options.Transports = HttpTransportType.WebSockets)
            .Build();

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
    }

    [Fact]
    public async Task A_cell_at_the_wrong_resolution_is_refused_rather_than_silently_joined()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await StartAsync();
        await using var connection = harness.PassengerConnection();
        await connection.StartAsync();

        // A res-5 id is a perfectly valid H3 cell — it is simply a group nothing publishes to. The
        // symptom would be an empty map, so the hub answers instead of accepting.
        var res5 = new H3Grid(GeoCells.DispatchResolution, 0).CellAt(Samples.ColomboFort);

        var error = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync(Contract.Methods.JoinGeocells, new[] { res5 }));

        Assert.Contains("resolution-7", error.Message, StringComparison.Ordinal);

        await connection.StopAsync();
    }

    [Fact]
    public async Task A_junk_cell_id_is_refused()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await StartAsync();
        await using var connection = harness.PassengerConnection();
        await connection.StartAsync();

        await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync(Contract.Methods.JoinGeocells, new[] { "not-a-cell" }));

        await connection.StopAsync();
    }

    [Fact]
    public async Task A_connection_may_not_ask_this_replica_to_poll_the_whole_country()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await StartAsync();
        await using var connection = harness.PassengerConnection();
        await connection.StartAsync();

        // JoinGeocells takes an array off the wire. Without a ceiling one client could put every
        // cell in Sri Lanka on this replica's poll list.
        var far = GeoCells.ViewCells(Samples.Kandy, ring: 8).Take(200).ToArray();

        await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync(Contract.Methods.JoinGeocells, far));

        await connection.StopAsync();
    }

    [Fact]
    public async Task Leaving_holds_the_group_for_the_hysteresis_window()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await StartAsync(hysteresis: TimeSpan.FromMilliseconds(300));
        await using var connection = harness.PassengerConnection();
        await connection.StartAsync();

        var cells = GeoCells.ViewCells(Samples.Dehiwala).Take(3).ToArray();
        await connection.InvokeAsync(Contract.Methods.JoinGeocells, cells);

        var subscriptions = harness.FanoutServices.GetRequiredService<ICellSubscriptions>();
        var connectionId = ConnectionIdOf(subscriptions, cells[0]);

        await connection.InvokeAsync(Contract.Methods.LeaveGeocells, cells);

        // ADD §7.4 step 6: still a member. A passenger walking along a cell edge would otherwise
        // join and leave the same six groups every few seconds, and each is a backplane round trip.
        Assert.Equal(3, subscriptions.CellsOf(connectionId).Count);
        Assert.Empty(subscriptions.DrainDueLeaves(DateTimeOffset.UtcNow));

        await Task.Delay(500);

        Assert.Equal(3, subscriptions.DrainDueLeaves(DateTimeOffset.UtcNow).Count);
        Assert.Empty(subscriptions.CellsOf(connectionId));

        await connection.StopAsync();
    }

    [Fact]
    public async Task Re_joining_inside_the_window_cancels_the_pending_removal()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await StartAsync(hysteresis: TimeSpan.FromMilliseconds(300));
        await using var connection = harness.PassengerConnection();
        await connection.StartAsync();

        var cells = GeoCells.ViewCells(Samples.Kandy).Take(2).ToArray();

        await connection.InvokeAsync(Contract.Methods.JoinGeocells, cells);
        await connection.InvokeAsync(Contract.Methods.LeaveGeocells, cells);

        // The oscillation case: the client crossed back before the window elapsed, so the
        // membership never lapsed and nothing is removed.
        await connection.InvokeAsync(Contract.Methods.JoinGeocells, cells);

        await Task.Delay(500);

        var subscriptions = harness.FanoutServices.GetRequiredService<ICellSubscriptions>();

        Assert.Empty(subscriptions.DrainDueLeaves(DateTimeOffset.UtcNow));
        Assert.All(cells, cell => Assert.Contains(cell, subscriptions.ActiveCells));

        await connection.StopAsync();
    }

    [Fact]
    public async Task A_disconnect_releases_the_cells_immediately_with_no_hysteresis()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await StartAsync(hysteresis: TimeSpan.FromMinutes(5));

        var cells = GeoCells.ViewCells(Samples.Dehiwala).Take(4).ToArray();
        var subscriptions = harness.FanoutServices.GetRequiredService<ICellSubscriptions>();

        await using (var connection = harness.PassengerConnection())
        {
            await connection.StartAsync();
            await connection.InvokeAsync(Contract.Methods.JoinGeocells, cells);

            Assert.All(cells, cell => Assert.Contains(cell, subscriptions.ActiveCells));

            await connection.StopAsync();
        }

        // No hysteresis on a disconnect: the socket is gone, so there is no membership worth
        // preserving and holding one would keep this replica polling streams for nobody.
        await WaitUntilAsync(
            () => cells.All(cell => !subscriptions.ActiveCells.Contains(cell)),
            "the cells should have been released when the connection dropped");
    }

    [Fact]
    public async Task Two_connections_in_one_cell_keep_it_active_until_both_are_gone()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await StartAsync();

        var cell = GeoCells.ViewCell(Samples.ColomboFort);
        var subscriptions = harness.FanoutServices.GetRequiredService<ICellSubscriptions>();

        await using var first = harness.PassengerConnection();
        await first.StartAsync();
        await first.InvokeAsync(Contract.Methods.JoinGeocells, new[] { cell });

        await using (var second = harness.PassengerConnection())
        {
            await second.StartAsync();
            await second.InvokeAsync(Contract.Methods.JoinGeocells, new[] { cell });
            await second.StopAsync();
        }

        // One batch per cell however many passengers are in it — that is ADD §7.4's cost model. The
        // cell has to stay on the poll list while anyone is still watching it.
        await Task.Delay(300);
        Assert.Contains(cell, subscriptions.ActiveCells);

        await first.StopAsync();

        await WaitUntilAsync(
            () => !subscriptions.ActiveCells.Contains(cell), "the cell should be released once nobody holds it");
    }

    private Task<HotPathHarness> StartAsync(TimeSpan? hysteresis = null) =>
        HotPathHarness.StartAsync(emqx, redpanda, redis, new HotPathHarnessOptions
        {
            Fanout = true,

            // The pump is off: these tests are about who is in which group, and a background push
            // arriving mid-assertion would make a membership question look like a delivery one.
            FanoutPump = false,
            JoinSeedFrames = 0,
            LeaveHysteresis = hysteresis,
        });

    /// <summary>
    /// The server-side connection id holding <paramref name="cell"/>, read back through the
    /// registry rather than taken from the client.
    /// </summary>
    /// <remarks>
    /// Asking the registry is asking the thing under test: the assertion is about what fanout-svc
    /// believes, not about what the client was told during negotiation.
    /// </remarks>
    private static string ConnectionIdOf(ICellSubscriptions subscriptions, string cell)
    {
        var connections = subscriptions.ConnectionsIn(cell);

        Assert.True(connections.Count > 0, $"No connection holds '{cell}'.");

        return connections.First();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Timed out waiting: {because}.");
    }
}
