using System.Collections.Concurrent;
using System.Threading.Channels;
using MageRide.HotPath.MqttBridge.Bridging;
using MageRide.HotPath.MqttBridge.Configuration;
using MageRide.Shared.Observability;
using Microsoft.Extensions.Options;

namespace MageRide.HotPath.MqttBridge.Throttling;

/// <summary>
/// Paces the backlog stream at T-05's per-device rate, one lane per vehicle.
/// </summary>
/// <remarks>
/// <para>
/// <b>A lane per device, not one queue.</b> The limit is per device, so a single queue drained in
/// order would let one vehicle's backlog block every other vehicle's behind it — head-of-line
/// blocking that turns a 20/s per-device limit into a 20/s limit for the whole replay stream. Each
/// lane waits on its own bucket, and they wait concurrently.
/// </para>
/// <para>
/// <b>Enqueueing never blocks the receive loop.</b> MQTTnet invokes the message handler on the
/// packet-processing task and delivers nothing else until it returns, so the wait for a token
/// happens on the lane, not on the handler.
/// </para>
/// <para>
/// <b>A shed sample is not acknowledged.</b> Whether the lane was full or the wait ran too long,
/// the bridge simply says nothing — EMQX still holds the message and redispatches it when this
/// session ends. Acknowledging a sample the bridge decided not to forward would make the throttle
/// silently lossy, which is the one thing a backlog stream must not be.
/// </para>
/// </remarks>
internal sealed class ReplayPacer : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, Lane> _lanes = new();
    private readonly ReplayThrottle _throttle;
    private readonly MqttBridgeOptions _options;
    private readonly Func<BridgedMessage, Task> _forward;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _sweeper;

    private long _queued;
    private long _completed;

    public ReplayPacer(
        ReplayThrottle throttle,
        IOptions<MqttBridgeOptions> options,
        Func<BridgedMessage, Task> forward,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _throttle = throttle ?? throw new ArgumentNullException(nameof(throttle));
        _options = options.Value;
        _forward = forward ?? throw new ArgumentNullException(nameof(forward));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sweeper = SweepAsync(_stopping.Token);
    }

    /// <summary>Samples accepted onto a lane but not yet forwarded or shed.</summary>
    public long Pending => Interlocked.Read(ref _queued) - Interlocked.Read(ref _completed);

    /// <summary>Samples that had to wait for a T-05 token. Non-zero means the throttle actually bit.</summary>
    public long Throttled => _throttle.Throttled;

    /// <summary>Accepts a backlog sample onto its device's lane, or refuses it so EMQX keeps it.</summary>
    public bool TryEnqueue(BridgedMessage bridged)
    {
        // Two attempts: a lane that closed between the lookup and the write is a sweep that raced an
        // arrival, and the second attempt gets its replacement.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var lane = _lanes.GetOrAdd(bridged.VehicleId, _ => new Lane(_options.ReplayQueueDepth));
            lane.EnsureDraining(() => DrainLaneAsync(bridged.VehicleId, lane, _stopping.Token));

            if (lane.Writer.TryWrite(bridged))
            {
                Interlocked.Increment(ref _queued);
                lane.Touch();
                return true;
            }

            if (!lane.Closed)
            {
                MageRideDiagnostics.MqttReplayShed.Add(
                    1, new KeyValuePair<string, object?>("reason", nameof(ReplayShedReason.QueueFull)));

                _logger.LogWarning(
                    "Replay lane for {VehicleId} is full at {Depth}; leaving the sample unacknowledged",
                    bridged.VehicleId, _options.ReplayQueueDepth);

                return false;
            }

            _lanes.TryRemove(new KeyValuePair<Guid, Lane>(bridged.VehicleId, lane));
        }

        return false;
    }

    /// <summary>Waits for every queued sample to be forwarded or shed.</summary>
    public async Task<bool> DrainAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (Pending > 0)
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(25);
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();

        foreach (var lane in _lanes.Values)
        {
            lane.Writer.TryComplete();
        }

        try
        {
            await Task.WhenAll([_sweeper, .. _lanes.Values.Select(lane => lane.Drain)]);
        }
        catch (OperationCanceledException)
        {
            // Expected: the lanes are cancelled, not asked politely.
        }

        _stopping.Dispose();
    }

    private async Task DrainLaneAsync(Guid vehicleId, Lane lane, CancellationToken cancellationToken)
    {
        // Yield so the lane is registered and this task assigned before anything runs on it.
        await Task.Yield();

        try
        {
            await foreach (var bridged in lane.Reader.ReadAllAsync(cancellationToken))
            {
                lane.Touch();

                try
                {
                    if (await _throttle.WaitAsync(vehicleId, cancellationToken))
                    {
                        await _forward(bridged);
                    }
                    else
                    {
                        MageRideDiagnostics.MqttReplayShed.Add(
                            1, new KeyValuePair<string, object?>("reason", nameof(ReplayShedReason.WaitTimeout)));

                        _logger.LogWarning(
                            "A replay sample for {VehicleId} waited past {MaxWait} for a T-05 token; " +
                            "leaving it unacknowledged",
                            vehicleId, _options.ReplayMaxWait);
                    }
                }
                finally
                {
                    Interlocked.Increment(ref _completed);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping. Anything still queued was never acknowledged, so EMQX still has it.
            DiscardRemaining(lane);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The replay lane for {VehicleId} stopped unexpectedly", vehicleId);
            DiscardRemaining(lane);
        }
    }

    private void DiscardRemaining(Lane lane)
    {
        while (lane.Reader.TryRead(out _))
        {
            Interlocked.Increment(ref _completed);
        }
    }

    /// <summary>Closes lanes nothing has used lately, so an idle fleet costs nothing.</summary>
    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var cutoff = DateTime.UtcNow - _options.ReplayLaneIdleTimeout;

                foreach (var (vehicleId, lane) in _lanes)
                {
                    if (lane.LastUsed > cutoff || lane.Reader.Count > 0)
                    {
                        continue;
                    }

                    lane.Closed = true;
                    lane.Writer.TryComplete();
                    _lanes.TryRemove(new KeyValuePair<Guid, Lane>(vehicleId, lane));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    /// <summary>One device's backlog queue and the single task draining it.</summary>
    private sealed class Lane(int depth)
    {
        private readonly Channel<BridgedMessage> _channel = Channel.CreateBounded<BridgedMessage>(
            new BoundedChannelOptions(depth)
            {
                SingleReader = true,

                // Wait, so a full lane makes TryWrite refuse rather than evict: an evicted sample
                // would have been acknowledged by nobody and forgotten by everybody. Refusing leaves
                // it with EMQX.
                FullMode = BoundedChannelFullMode.Wait,
            });

        private readonly Lock _gate = new();

        private long _lastUsedTicks = DateTime.UtcNow.Ticks;

        public ChannelWriter<BridgedMessage> Writer => _channel.Writer;

        public ChannelReader<BridgedMessage> Reader => _channel.Reader;

        public Task Drain { get; private set; } = Task.CompletedTask;

        public bool Closed { get; set; }

        public DateTime LastUsed => new(Interlocked.Read(ref _lastUsedTicks), DateTimeKind.Utc);

        /// <summary>
        /// Starts the drain exactly once. <c>GetOrAdd</c> may run its factory on several threads and
        /// keep only one result, so the task cannot be started there — the losers' would read from a
        /// channel nobody writes to and never end.
        /// </summary>
        public void EnsureDraining(Func<Task> start)
        {
            lock (_gate)
            {
                if (Drain == Task.CompletedTask)
                {
                    Drain = start();
                }
            }
        }

        public void Touch() => Interlocked.Exchange(ref _lastUsedTicks, DateTime.UtcNow.Ticks);
    }
}
