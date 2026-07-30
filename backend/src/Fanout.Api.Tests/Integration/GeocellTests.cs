using MageRide.Fanout.Realtime;
using MageRide.Fanout.Tests.Infrastructure;
using MageRide.Shared.Geo;
using MageRide.TestKit;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Contract = MageRide.Shared.Realtime.LiveHub;

namespace MageRide.Fanout.Tests.Integration;

/// <summary>
/// The public geocell plane: R-06's nineteen cells, the res-7 fence and ADD §7.4 step 6's
/// hysteresis.
/// </summary>
/// <remarks>
/// Every connection is a real SignalR client over a real WebSocket against a real Kestrel, so the
/// <c>access_token</c> query authentication, the JSON hub protocol and the group bookkeeping are all
/// the ones the apps meet. A hub method invoked in-process would exercise none of them.
/// </remarks>
[Collection<FanoutCollection>]
public sealed class GeocellTests(RedisFixture redis, RedpandaFixture redpanda, EmqxFixture emqx)
{
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

        await using var connection = harness.Passenger(Guid.NewGuid());
        await connection.StartAsync();
        await connection.InvokeAsync(Contract.Methods.JoinGeocells, cells.ToArray());

        var subscriptions = harness.Services.GetRequiredService<ICellSubscriptions>();

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
                new Uri(new Uri(BaseAddressOf(harness)), Contract.Path),
                options => options.Transports = HttpTransportType.WebSockets)
            .Build();

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
    }

    [Fact]
    public async Task A_cell_at_the_wrong_resolution_is_refused_rather_than_silently_joined()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await StartAsync();
        await using var connection = harness.Passenger(Guid.NewGuid());
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
        await using var connection = harness.Passenger(Guid.NewGuid());
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
        await using var connection = harness.Passenger(Guid.NewGuid());
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
        await using var connection = harness.Passenger(Guid.NewGuid());
        await connection.StartAsync();

        var cells = GeoCells.ViewCells(Samples.Dehiwala).Take(3).ToArray();
        await connection.InvokeAsync(Contract.Methods.JoinGeocells, cells);

        var subscriptions = harness.Services.GetRequiredService<ICellSubscriptions>();
        var connectionId = ConnectionIdOf(subscriptions, cells[0]);

        await connection.InvokeAsync(Contract.Methods.LeaveGeocells, cells);

        // ADD §7.4 step 6: still a member. A passenger walking along a cell edge would otherwise
        // join and leave the same six groups every few seconds.
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
        await using var connection = harness.Passenger(Guid.NewGuid());
        await connection.StartAsync();

        var cells = GeoCells.ViewCells(Samples.Kandy).Take(2).ToArray();

        await connection.InvokeAsync(Contract.Methods.JoinGeocells, cells);
        await connection.InvokeAsync(Contract.Methods.LeaveGeocells, cells);

        // The oscillation case: the client crossed back before the window elapsed, so the
        // membership never lapsed and nothing is removed.
        await connection.InvokeAsync(Contract.Methods.JoinGeocells, cells);

        await Task.Delay(500);

        var subscriptions = harness.Services.GetRequiredService<ICellSubscriptions>();

        Assert.Empty(subscriptions.DrainDueLeaves(DateTimeOffset.UtcNow));
        Assert.All(cells, cell => Assert.Contains(cell, subscriptions.ActiveCells));

        await connection.StopAsync();
    }

    /// <summary>
    /// DoD: "a passenger crossing a cell boundary re-subscribes with 30 s hysteresis and sees no gap."
    /// </summary>
    /// <remarks>
    /// The gap is the thing being ruled out, and it is not about group bookkeeping — it is about
    /// frames. A passenger who steps across an edge leaves six cells and joins six others; if the
    /// leave took effect at once, every vehicle in the cells behind them would vanish for as long as
    /// the client took to notice and re-join. So the vehicle in the cell they are walking out of is
    /// still delivered after the leave, and stops only once the window has actually elapsed.
    /// </remarks>
    [Fact]
    public async Task A_passenger_crossing_a_boundary_keeps_receiving_the_cells_behind_them()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await StartAsync(hysteresis: TimeSpan.FromSeconds(3));

        var behind = Samples.ColomboFort;
        var vehicleId = Guid.NewGuid();
        var events = new LiveEvents();

        await using var passenger = harness.Passenger(Guid.NewGuid());
        events.Attach(passenger);
        await passenger.StartAsync();

        var before = GeoCells.ViewCells(behind).ToArray();
        await passenger.InvokeAsync(Contract.Methods.JoinGeocells, before);

        // The passenger walks on: the client joins the new view and releases the old one, which is
        // exactly what an app crossing an edge does.
        await passenger.InvokeAsync(
            Contract.Methods.JoinGeocells, GeoCells.ViewCells(Samples.Dehiwala).ToArray());

        await passenger.InvokeAsync(Contract.Methods.LeaveGeocells, before);

        await harness.Positions.PublishAsync(vehicleId, behind);
        await harness.CellsAsync();

        await FanoutHarness.WaitAsync(
            () => events.Saw(vehicleId), "the cell behind the passenger should still be delivering");

        events.Clear();

        // Past the window. The pump applies the due removals on its own tick.
        await Task.Delay(TimeSpan.FromSeconds(3.5));
        await harness.CellsAsync();

        await harness.Positions.PublishAsync(vehicleId, behind, seq: 2);
        await harness.CellsAsync();
        await Task.Delay(300);

        Assert.False(events.Saw(vehicleId), "the cell should have been released once the window elapsed");

        await passenger.StopAsync();
    }

    [Fact]
    public async Task A_disconnect_releases_the_cells_immediately_with_no_hysteresis()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await StartAsync(hysteresis: TimeSpan.FromMinutes(5));

        var cells = GeoCells.ViewCells(Samples.Dehiwala).Take(4).ToArray();
        var subscriptions = harness.Services.GetRequiredService<ICellSubscriptions>();

        await using (var connection = harness.Passenger(Guid.NewGuid()))
        {
            await connection.StartAsync();
            await connection.InvokeAsync(Contract.Methods.JoinGeocells, cells);

            Assert.All(cells, cell => Assert.Contains(cell, subscriptions.ActiveCells));

            await connection.StopAsync();
        }

        // No hysteresis on a disconnect: the socket is gone, so there is no membership worth
        // preserving and holding one would keep this replica polling streams for nobody.
        await FanoutHarness.WaitAsync(
            () => cells.All(cell => !subscriptions.ActiveCells.Contains(cell)),
            "the cells should have been released when the connection dropped");
    }

    [Fact]
    public async Task Two_connections_in_one_cell_keep_it_active_until_both_are_gone()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await StartAsync();

        var cell = GeoCells.ViewCell(Samples.ColomboFort);
        var subscriptions = harness.Services.GetRequiredService<ICellSubscriptions>();

        await using var first = harness.Passenger(Guid.NewGuid());
        await first.StartAsync();
        await first.InvokeAsync(Contract.Methods.JoinGeocells, new[] { cell });

        await using (var second = harness.Passenger(Guid.NewGuid()))
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

        await FanoutHarness.WaitAsync(
            () => !subscriptions.ActiveCells.Contains(cell), "the cell should be released once nobody holds it");
    }

    private Task<FanoutHarness> StartAsync(TimeSpan? hysteresis = null) =>
        FanoutHarness.StartAsync(redis, redpanda, emqx, new FanoutHarnessOptions
        {
            // The pumps are stepped by hand: these tests are about who is in which group, and a
            // background push arriving mid-assertion would make a membership question look like a
            // delivery one.
            Pump = false,
            LeaveHysteresis = hysteresis,

            // No broker: nothing here is about an event. Keeping Kafka out means these tests fail
            // for group-membership reasons or not at all.
            Events = false,
            ControlPlane = false,
        });

    private static string BaseAddressOf(FanoutHarness harness)
    {
        var addresses = harness.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!;

        return addresses.Addresses.First();
    }

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
}
