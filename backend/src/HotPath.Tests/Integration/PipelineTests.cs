using System.Diagnostics;
using System.Text.Json;
using MageRide.HotPath.Tests.Infrastructure;
using MageRide.Shared.Geo;
using MageRide.Shared.Realtime;
using MageRide.TestKit;
using Microsoft.AspNetCore.SignalR.Client;
using Contract = MageRide.Shared.Realtime.LiveHub;

namespace MageRide.HotPath.Tests.Integration;

/// <summary>
/// DoD: "a position published to EMQX appears on the passenger's SignalR group in under 5 s (p95)."
/// </summary>
/// <remarks>
/// <para>
/// The whole slice, on real infrastructure: a driver's MQTT client publishes CBOR to EMQX, the
/// bridge's shared subscription lifts it onto Redpanda, position-processor normalises it into the
/// <c>cell:{h3index}</c> stream, and the pump pushes it to the res-7 group the passenger joined.
/// Five processes and three brokers, and nothing in the chain is faked — which is the point of a
/// walking skeleton.
/// </para>
/// <para>
/// The pump runs at its <b>shipped default</b> (2 s, the floor of <c>signalr-hub.md</c> §3's 2–8 s
/// band). Timing the SLO against a shortened interval would prove a configuration nobody deploys.
/// </para>
/// </remarks>
[Collection<HotPathCollection>]
public sealed class PipelineTests(EmqxFixture emqx, RedpandaFixture redpanda, RedisFixture redis)
{
    /// <summary>The SLO this component is measured against.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    /// <summary>Generous enough that a slow container start is not read as a pipeline failure.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    /// <summary>The DoD assertion.</summary>
    [Fact]
    public async Task A_position_published_to_EMQX_reaches_the_passengers_geocell_group()
    {
        RequireContainers();

        await using var harness = await StartAsync();
        await harness.WaitForBridgesAsync();

        var vehicleId = Guid.NewGuid();
        var frames = new FrameCollector();

        await using var passenger = harness.PassengerConnection();
        frames.Attach(passenger);
        await passenger.StartAsync();

        // The passenger's 3 km view: res-7 self + ring(2) = 19 cells (R-06). Joined *before* the
        // driver publishes, which is the ordinary case — a passenger opens the map, then vehicles
        // report.
        var view = GeoCells.ViewCells(Samples.ColomboFort);
        Assert.Equal(19, view.Count);

        await passenger.InvokeAsync(Contract.Methods.JoinGeocells, view.ToArray());

        await using var device = await DeviceClient.ConnectAsync(emqx, vehicleId);

        var clock = Stopwatch.StartNew();
        await device.PublishPositionAsync(Samples.At(vehicleId, Samples.ColomboFort, seq: 1));

        var frame = await frames.WaitForAsync(vehicleId, Timeout);
        clock.Stop();

        Assert.Equal(Samples.ColomboFort.Latitude, frame.Lat, precision: 4);
        Assert.Equal(Samples.ColomboFort.Longitude, frame.Lng, precision: 4);
        Assert.Equal("three_wheeler", frame.Type);
        Assert.Equal("C", frame.Mode);
        Assert.Equal(90, frame.Heading);

        Assert.True(
            clock.Elapsed < Budget,
            $"EMQX to SignalR took {clock.Elapsed.TotalSeconds:F2} s; the SLO is under {Budget.TotalSeconds} s.");

        await passenger.StopAsync();
    }

    /// <summary>
    /// The DoD says <b>p95</b>, and one observation is not a p95.
    /// </summary>
    /// <remarks>
    /// Twenty positions spaced a second apart, so they fall across different batch windows rather
    /// than all landing in one — measuring twenty vehicles published at the same instant would give
    /// twenty numbers and one sample. Each vehicle's latency is measured from its own publish to
    /// the frame carrying it.
    /// </remarks>
    [Fact]
    public async Task The_p95_latency_from_EMQX_to_the_passenger_is_under_5_seconds()
    {
        RequireContainers();

        await using var harness = await StartAsync();
        await harness.WaitForBridgesAsync();

        var frames = new FrameCollector();

        await using var passenger = harness.PassengerConnection();
        frames.Attach(passenger);
        await passenger.StartAsync();
        await passenger.InvokeAsync(
            Contract.Methods.JoinGeocells, GeoCells.ViewCells(Samples.ColomboFort).ToArray());

        const int samples = 20;
        var published = new Dictionary<Guid, long>(samples);
        var devices = new List<DeviceClient>(samples);

        try
        {
            for (var i = 0; i < samples; i++)
            {
                var vehicleId = Guid.NewGuid();
                var device = await DeviceClient.ConnectAsync(emqx, vehicleId);
                devices.Add(device);

                published[vehicleId] = frames.Now;
                await device.PublishPositionAsync(Samples.At(vehicleId, Samples.ColomboFort, seq: 1));

                await Task.Delay(1_000);
            }

            var latencies = new List<double>(samples);

            foreach (var (vehicleId, publishedAt) in published)
            {
                await frames.WaitForAsync(vehicleId, Timeout);
                latencies.Add(frames.FirstSeenAt(vehicleId) - publishedAt);
            }

            latencies.Sort();

            // Nearest-rank p95 over 20 observations is the 19th.
            var p95 = latencies[(int)Math.Ceiling(0.95 * latencies.Count) - 1];

            Assert.True(
                p95 < Budget.TotalMilliseconds,
                $"p95 was {p95:F0} ms over {latencies.Count} positions; the SLO is under " +
                $"{Budget.TotalMilliseconds:F0} ms. Median {latencies[latencies.Count / 2]:F0} ms, " +
                $"max {latencies[^1]:F0} ms.");
        }
        finally
        {
            foreach (var device in devices)
            {
                await device.DisposeAsync();
            }

            await passenger.StopAsync();
        }
    }

    [Fact]
    public async Task A_passenger_outside_the_vehicles_cell_hears_nothing_about_it()
    {
        RequireContainers();

        await using var harness = await StartAsync();
        await harness.WaitForBridgesAsync();

        var vehicleId = Guid.NewGuid();
        var frames = new FrameCollector();

        await using var passenger = harness.PassengerConnection();
        frames.Attach(passenger);
        await passenger.StartAsync();

        // Kandy is ~95 km from Colombo, well outside a 19-cell res-7 view. This is the cost model
        // ADD §7.4 exists for: fan-out is per cell, so a passenger's socket carries the vehicles
        // near them and nothing else.
        await passenger.InvokeAsync(
            Contract.Methods.JoinGeocells, GeoCells.ViewCells(Samples.Kandy).ToArray());

        await using var device = await DeviceClient.ConnectAsync(emqx, vehicleId);
        await device.PublishPositionAsync(Samples.At(vehicleId, Samples.ColomboFort, seq: 1));

        // Long enough for several pump ticks. If the position were going to arrive, it would have.
        await Task.Delay(TimeSpan.FromSeconds(8));

        Assert.DoesNotContain(vehicleId, frames.Seen);

        await passenger.StopAsync();
    }

    [Fact]
    public async Task A_moving_vehicle_arrives_as_a_batch_carrying_its_newest_position()
    {
        RequireContainers();

        await using var harness = await StartAsync();
        await harness.WaitForBridgesAsync();

        var vehicleId = Guid.NewGuid();
        var frames = new FrameCollector();

        await using var passenger = harness.PassengerConnection();
        frames.Attach(passenger);
        await passenger.StartAsync();
        await passenger.InvokeAsync(
            Contract.Methods.JoinGeocells, GeoCells.ViewCells(Samples.Dehiwala).ToArray());

        await using var device = await DeviceClient.ConnectAsync(emqx, vehicleId);

        // Six samples inside one batch window, each a little further east.
        for (var seq = 1; seq <= 6; seq++)
        {
            var point = new Shared.Primitives.GeoPoint(
                Samples.Dehiwala.Latitude, Samples.Dehiwala.Longitude + (seq * 0.0002));

            await device.PublishPositionAsync(Samples.At(vehicleId, point, seq));
        }

        var frame = await frames.WaitForAsync(vehicleId, Timeout);

        // The batch carries the newest frame per vehicle, not the history: a vehicle is in exactly
        // one place now, and replaying its last six fixes would make the marker jitter backwards.
        await WaitUntilAsync(
            () => frames.Latest(vehicleId)?.Lng >= Samples.Dehiwala.Longitude + 0.0011,
            "the passenger should end up with the vehicle's newest position");

        Assert.True(frame.Lng >= Samples.Dehiwala.Longitude);

        // And a batch is a batch: six fixes did not become six pushes.
        Assert.True(
            frames.Batches < 6,
            $"Six samples produced {frames.Batches} pushes; VehiclePositions is batched per cell, not per fix.");

        await passenger.StopAsync();
    }

    [Fact]
    public async Task A_passenger_joining_a_populated_cell_is_seeded_with_what_is_already_there()
    {
        RequireContainers();

        await using var harness = await StartAsync(seedFrames: 32);
        await harness.WaitForBridgesAsync();

        var vehicleId = Guid.NewGuid();

        await using var device = await DeviceClient.ConnectAsync(emqx, vehicleId);
        await device.PublishPositionAsync(Samples.At(vehicleId, Samples.ColomboFort, seq: 1));

        // Wait for the position to reach the cell stream before anyone is watching it.
        await using var connection = await harness.ConnectRedisAsync();
        var cell = GeoCells.ViewCell(Samples.ColomboFort);

        await WaitUntilAsync(
            async () =>
            {
                var entries = await connection.GetDatabase().StreamRangeAsync(
                    Shared.Caching.RedisKeys.Cell(cell), minId: "-", maxId: "+", count: 200);

                return entries.Any(entry => entry.Values.Any(
                    value => value.Value.ToString() == vehicleId.ToString()));
            },
            "the sample should reach the cell stream");

        var frames = new FrameCollector();
        await using var passenger = harness.PassengerConnection();
        frames.Attach(passenger);
        await passenger.StartAsync();

        await passenger.InvokeAsync(
            Contract.Methods.JoinGeocells, GeoCells.ViewCells(Samples.ColomboFort).ToArray());

        // Without the seed the passenger would stare at an empty map until each nearby vehicle's
        // next sample. `signalr-hub.md` §1.1 assigns that snapshot to query-svc's GET /v1/nearby
        // (C042); Fanout:JoinSeedFrames is the bounded stand-in until it exists, and C041/C042
        // should remove it then.
        var frame = await frames.WaitForAsync(vehicleId, Timeout);

        Assert.Equal(Samples.ColomboFort.Latitude, frame.Lat, precision: 4);

        await passenger.StopAsync();
    }

    private Task<HotPathHarness> StartAsync(int seedFrames = 0) =>
        HotPathHarness.StartAsync(emqx, redpanda, redis, new HotPathHarnessOptions
        {
            BridgeReplicas = 1,
            Processor = true,
            Fanout = true,
            FanoutPump = true,

            // Deliberately the shipped default (2 s). See the class remarks.
            BatchInterval = null,
            JoinSeedFrames = seedFrames,
        });

    private void RequireContainers()
    {
        Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because) =>
        await WaitUntilAsync(() => Task.FromResult(condition()), because);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string because)
    {
        var deadline = DateTime.UtcNow + Timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(150);
        }

        Assert.Fail($"Timed out waiting: {because}.");
    }

    /// <summary>
    /// Collects <c>VehiclePositions</c> batches off a real hub connection.
    /// </summary>
    /// <remarks>
    /// The payload is read as <see cref="JsonElement"/> rather than bound to
    /// <see cref="VehicleFrame"/>, so the property names on the wire are asserted rather than
    /// assumed — SignalR resolves them by string, and a rename would otherwise deserialise into
    /// silent defaults.
    /// </remarks>
    private sealed class FrameCollector
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<Guid, VehicleFrame> _latest = [];
        private readonly Dictionary<Guid, long> _firstSeenAt = [];
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public int Batches { get; private set; }

        /// <summary>
        /// Milliseconds on the collector's own clock. Publish times are stamped from here too, so a
        /// latency is a difference on one clock rather than across two started moments apart.
        /// </summary>
        public long Now => _clock.ElapsedMilliseconds;

        public IReadOnlyCollection<Guid> Seen
        {
            get
            {
                lock (_gate)
                {
                    return [.. _latest.Keys];
                }
            }
        }

        public void Attach(HubConnection connection) =>
            connection.On<JsonElement>(Contract.Events.VehiclePositions, Record);

        public VehicleFrame? Latest(Guid vehicleId)
        {
            lock (_gate)
            {
                return _latest.TryGetValue(vehicleId, out var frame) ? frame : null;
            }
        }

        /// <summary>
        /// Milliseconds on this collector's clock when a vehicle was first delivered. Paired with a
        /// publish time taken from a clock started at the same moment, so the difference is the
        /// end-to-end latency and not a wall-clock subtraction across two machines.
        /// </summary>
        public long FirstSeenAt(Guid vehicleId)
        {
            lock (_gate)
            {
                return _firstSeenAt.TryGetValue(vehicleId, out var at)
                    ? at
                    : throw new InvalidOperationException($"Vehicle {vehicleId} was never delivered.");
            }
        }

        public async Task<VehicleFrame> WaitForAsync(Guid vehicleId, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                if (Latest(vehicleId) is { } frame)
                {
                    return frame;
                }

                await Task.Delay(50);
            }

            throw new TimeoutException(
                $"Vehicle {vehicleId} never reached the passenger's group. Seen: [{string.Join(", ", Seen)}].");
        }

        private void Record(JsonElement batch)
        {
            lock (_gate)
            {
                Batches++;

                foreach (var element in batch.EnumerateArray())
                {
                    var frame = new VehicleFrame(
                        element.GetProperty("vehicleId").GetGuid(),
                        element.GetProperty("lat").GetDouble(),
                        element.GetProperty("lng").GetDouble(),
                        Read(element, "heading")?.GetInt32(),
                        Read(element, "speed")?.GetDouble(),
                        Read(element, "type")?.GetString(),
                        Read(element, "mode")?.GetString());

                    _latest[frame.VehicleId] = frame;
                    _firstSeenAt.TryAdd(frame.VehicleId, _clock.ElapsedMilliseconds);
                }
            }
        }

        private static JsonElement? Read(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind is not JsonValueKind.Null ? value : null;
    }
}
