using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using MageRide.Shared.Geo;
using MageRide.Shared.Primitives;
using Microsoft.AspNetCore.SignalR.Client;
using Contract = MageRide.Shared.Realtime.LiveHub;

namespace MageRide.E2E.Infrastructure;

/// <summary>
/// A passenger's or driver's <c>/hubs/live</c> connection, over a real WebSocket.
/// </summary>
/// <remarks>
/// <para>
/// The plane the apps watch a ride on. The access token goes in the query string, which is
/// SignalR's convention and unavoidable — a browser <c>WebSocket</c> cannot set an
/// <c>Authorization</c> header (<c>signalr-hub.md</c> §1) — so this is the path a real client takes,
/// authentication hook included.
/// </para>
/// <para>
/// <b>What a scenario proves with it.</b> <c>SubscribeRide</c> is participant-checked against
/// fanout-svc's projection of <c>ride.events</c>, so a connection that can join at all is evidence
/// the booking travelled ride-svc → outbox → Redpanda → fanout-svc; and every
/// <see cref="States"/> entry after that is a state change that made the same journey. The passenger
/// never polls ride-svc for it, which is the point.
/// </para>
/// </remarks>
internal sealed class LiveConnection : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly ConcurrentQueue<(string State, long Version)> _states = new();
    private readonly ConcurrentQueue<Guid> _positions = new();
    private readonly ConcurrentQueue<Guid> _revocations = new();
    private readonly ConcurrentQueue<JsonElement> _resolutions = new();

    private LiveConnection(HubConnection connection)
    {
        _connection = connection;

        connection.On<JsonElement>(Contract.Events.RideStateChanged, frame =>
            _states.Enqueue((
                frame.GetProperty("state").GetString() ?? string.Empty,
                frame.GetProperty("version").GetInt64())));

        // C121's two. A Mode B watcher is served on `vehicle:{vehicleId}`, whose membership the
        // server derives from `share:{userId}` — so what arrives here is the D-23 entitlement
        // working, and what stops arriving is D-22's revocation.
        connection.On<JsonElement>(Contract.Events.VehiclePositions, batch =>
        {
            foreach (var frame in batch.EnumerateArray())
            {
                _positions.Enqueue(frame.GetProperty("vehicleId").GetGuid());
            }
        });

        connection.On<JsonElement>(Contract.Events.ShareRevoked, frame =>
            _revocations.Enqueue(frame.GetProperty("vehicleId").GetGuid()));

        // C122's one. P-13's whole claim is that the booker learns the answer without polling, so
        // the frame is kept whole — a decline that arrived carrying a coordinate is the failure this
        // is watching for, and a handler that only read `state` could not see it.
        connection.On<JsonElement>(Contract.Events.LocationRequestResolved, frame =>
            _resolutions.Enqueue(frame.Clone()));
    }

    /// <summary>Every <c>RideStateChanged</c> this connection has been pushed, in arrival order.</summary>
    public IReadOnlyList<(string State, long Version)> States => [.. _states];

    /// <summary>Every vehicle this connection has been shown a position for, in arrival order.</summary>
    public IReadOnlyList<Guid> Positions => [.. _positions];

    /// <summary>Every <c>ShareRevoked</c> this connection has been pushed (D-22).</summary>
    public IReadOnlyList<Guid> Revocations => [.. _revocations];

    /// <summary>Every <c>LocationRequestResolved</c> frame, whole, in arrival order (P-13).</summary>
    public IReadOnlyList<JsonElement> Resolutions => [.. _resolutions];

    public static Task<LiveConnection> OpenAsync(ModeCFleet fleet, string bearer)
    {
        ArgumentNullException.ThrowIfNull(fleet);

        return OpenAsync(fleet.FanoutBaseAddress, bearer);
    }

    public static Task<LiveConnection> OpenAsync(ProxyPackageFleet fleet, string bearer)
    {
        ArgumentNullException.ThrowIfNull(fleet);

        return OpenAsync(fleet.FanoutBaseAddress, bearer);
    }

    public static Task<LiveConnection> OpenAsync(ModeAbFleet fleet, string bearer)
    {
        ArgumentNullException.ThrowIfNull(fleet);

        return OpenAsync(fleet.FanoutBaseAddress, bearer);
    }

    private static async Task<LiveConnection> OpenAsync(Uri fanoutBaseAddress, string bearer)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(fanoutBaseAddress, Contract.Path),
                options =>
                {
                    options.Transports = HttpTransportType.WebSockets;
                    options.AccessTokenProvider = () => Task.FromResult<string?>(bearer);
                })
            .Build();

        var live = new LiveConnection(connection);
        await connection.StartAsync(TestContext.Current.CancellationToken);

        return live;
    }

    /// <summary>
    /// Joins <c>ride:{rideId}</c>, retrying while fanout-svc's projection catches up.
    /// </summary>
    /// <remarks>
    /// The retry is not politeness. The participant check reads a projection built from
    /// <c>ride.events</c>, and a passenger who books and subscribes in the same breath is racing
    /// their own <c>ride.requested</c> across the broker — which is exactly what the real app does
    /// and exactly what <c>signalr-hub.md</c> tells it to retry.
    /// </remarks>
    public async Task SubscribeAsync(Guid rideId, TimeSpan? within = null)
    {
        var deadline = DateTimeOffset.UtcNow + (within ?? TimeSpan.FromSeconds(30));
        Exception? refused = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await _connection.InvokeAsync(
                    Contract.Methods.SubscribeRide, rideId.ToString(), TestContext.Current.CancellationToken);

                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                refused = exception;
                await Task.Delay(200, TestContext.Current.CancellationToken);
            }
        }

        Assert.Fail($"fanout-svc never admitted the participant to ride {rideId}: {refused?.Message}");
    }

    /// <summary>
    /// Joins the nineteen res-7 cells a 3 km passenger view is (R-06).
    /// </summary>
    /// <remarks>
    /// The client computes them and the server does not infer a view from a position — the client is
    /// the only side that knows which view it is showing. Joining also re-resolves the caller's
    /// entitlements, which is D-23's "checked on group join" taken literally: a grant accepted since
    /// the socket opened takes effect at the next map interaction as well as on the control channel,
    /// so the two paths cover each other.
    /// </remarks>
    public Task JoinAsync(GeoPoint centre) =>
        _connection.InvokeAsync(
            Contract.Methods.JoinGeocells,
            GeoCells.ViewCells(centre).ToArray(),
            TestContext.Current.CancellationToken);

    /// <summary>
    /// Joins <c>booker:{bookerId}:loc-req:{requestId}</c> — the group the booker's app is on while
    /// it waits (P-13).
    /// </summary>
    /// <remarks>
    /// No retry, unlike <see cref="SubscribeAsync"/>: there is nothing to catch up with. The group
    /// name is built from the caller's own subject and the request id, so fanout-svc admits the
    /// booker without reading anything — which it must, because the round-trip happens before a ride
    /// exists and there is no projection yet to have been admitted by.
    /// </remarks>
    public Task SubscribeLocationRequestAsync(Guid requestId) =>
        _connection.InvokeAsync(
            Contract.Methods.SubscribeLocRequest, requestId.ToString(), TestContext.Current.CancellationToken);

    /// <summary>
    /// Waits for the round-trip's answer and returns the whole frame.
    /// </summary>
    /// <remarks>
    /// The frame rather than the state, so a caller can assert on what is <em>not</em> in it. A
    /// declined request that arrived with a <c>geo</c> member would satisfy any assertion written
    /// about <c>state</c> alone, and that is precisely P-02's failure mode.
    /// </remarks>
    public async Task<JsonElement> AwaitResolutionAsync(Guid requestId, TimeSpan? within = null)
    {
        var deadline = DateTimeOffset.UtcNow + (within ?? TimeSpan.FromSeconds(30));

        while (DateTimeOffset.UtcNow < deadline)
        {
            foreach (var frame in _resolutions)
            {
                if (frame.TryGetProperty("requestId", out var id)
                    && id.GetGuid() == requestId)
                {
                    return frame;
                }
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Fail(
            $"The booker's socket was never pushed a resolution for location request {requestId}. "
            + "P-13 says this arrives without polling, so nothing else was going to tell them.");

        return default;
    }

    /// <summary>Waits until this connection has been shown a position for <paramref name="vehicleId"/>.</summary>
    public Task<bool> SawVehicleAsync(Guid vehicleId, TimeSpan? within = null) =>
        SawAsync(() => _positions.Contains(vehicleId), within);

    /// <summary>Waits until this connection has been told the grant on <paramref name="vehicleId"/> is gone.</summary>
    public Task<bool> SawRevocationAsync(Guid vehicleId, TimeSpan? within = null) =>
        SawAsync(() => _revocations.Contains(vehicleId), within);

    /// <summary>Forgets everything seen so far, so "stopped arriving" can be asserted after a change.</summary>
    public void Forget()
    {
        _positions.Clear();
        _revocations.Clear();
    }

    /// <summary>Waits until this connection has been pushed <paramref name="state"/>.</summary>
    public async Task<bool> SawAsync(string state, TimeSpan? within = null)
    {
        var deadline = DateTimeOffset.UtcNow + (within ?? TimeSpan.FromSeconds(30));

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_states.Any(frame => string.Equals(frame.State, state, StringComparison.Ordinal)))
            {
                return true;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        return false;
    }

    private static async Task<bool> SawAsync(Func<bool> condition, TimeSpan? within)
    {
        var deadline = DateTimeOffset.UtcNow + (within ?? TimeSpan.FromSeconds(30));

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        return false;
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
