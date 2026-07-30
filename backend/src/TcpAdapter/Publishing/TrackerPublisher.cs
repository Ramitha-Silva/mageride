using System.Diagnostics;
using System.Text;
using MageRide.Shared.Mqtt;
using MageRide.Shared.Telemetry;
using MageRide.TcpAdapter.Configuration;
using MageRide.TcpAdapter.Observability;
using MageRide.TcpAdapter.Protocols;
using Microsoft.Extensions.Options;

namespace MageRide.TcpAdapter.Publishing;

/// <summary>Publishes a tracker's samples and its presence into the MQTT plane.</summary>
public interface ITrackerPublisher
{
    /// <summary>
    /// Publishes one sample to <c>pos/live</c> or <c>pos/replay</c>.
    /// </summary>
    /// <param name="sample">The canonical sample.</param>
    /// <param name="family">Which adapter produced it, for the metric label.</param>
    /// <param name="replay">Whether it is backlog (T-05).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<bool> PublishSampleAsync(
        PositionSample sample, ProtocolFamily family, bool replay, CancellationToken cancellationToken);

    /// <summary>
    /// Publishes the retained <c>veh/{vehicleId}/status</c> — the T-04 last-will emulation.
    /// </summary>
    Task<bool> PublishPresenceAsync(Guid vehicleId, bool online, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITrackerPublisher"/>
/// <remarks>
/// <para>
/// <b>The payload is CBOR, encoded by the shared codec.</b> Not JSON, and not a shape of this
/// service's own: <c>mqtt-topics.md</c> §2.1 gives one payload for the whole telemetry plane and
/// position-processor-svc decodes what the driver app encodes. A tracker sample that arrived as
/// sixty-eight bytes of GT06 leaves here indistinguishable from a handset's — which is what makes
/// T-11's routing and R-17's dedupe work on both without knowing the difference.
/// </para>
/// <para>
/// <b><c>pos/live</c> is published retained and <c>pos/replay</c> is not</b>, exactly as §3.1's table
/// prints it: a consumer subscribing after the fact gets a position immediately rather than waiting a
/// cadence, and a backlog sample must never become the retained "current" one. This does not duplicate
/// ingest at the bridge, because MQTT 5 forbids delivering retained messages to a shared subscription
/// and <c>$share/posGroup/…</c> is how mqtt-bridge-svc consumes (E-08).
/// </para>
/// </remarks>
public sealed class TrackerPublisher(
    EmqxLink link, IOptions<AdapterOptions> options, ILogger<TrackerPublisher> logger) : ITrackerPublisher
{
    private readonly AdapterOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<bool> PublishSampleAsync(
        PositionSample sample, ProtocolFamily family, bool replay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sample);

        var topic = replay
            ? MqttTopics.PositionReplay(sample.VehicleId)
            : MqttTopics.PositionLive(sample.VehicleId);

        var published = await link.PublishAsync(
            topic, PositionSampleCodec.Encode(sample), retain: !replay, cancellationToken);

        var tags = new[]
        {
            AdapterDiagnostics.Tag("family", ProtocolFamilies.Name(family)),
            AdapterDiagnostics.Tag("stream", replay ? "replay" : "live"),
        };

        if (published)
        {
            AdapterDiagnostics.SamplesPublished.Add(1, tags);
        }
        else
        {
            AdapterDiagnostics.SamplesGated.Add(
                1, AdapterDiagnostics.Tag("reason", "broker_unavailable"), tags[0]);
        }

        return published;
    }

    public async Task<bool> PublishPresenceAsync(Guid vehicleId, bool online, CancellationToken cancellationToken)
    {
        if (!_options.PublishPresence)
        {
            return false;
        }

        var state = online ? VehicleStatus.Online : VehicleStatus.Offline;
        var started = Stopwatch.GetTimestamp();

        // The whole point of T-04 is that this lands inside a bounded window: the device has already
        // gone, so there is nothing to retry against and a publish that hangs on a dead broker
        // connection would hold the session's teardown open instead.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.OfflineWindow);

        bool published;

        try
        {
            published = await link.PublishAsync(
                MqttTopics.Status(vehicleId), Encoding.UTF8.GetBytes(state), retain: true, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            published = false;
        }

        if (!online)
        {
            AdapterDiagnostics.OfflineLatencyMs.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        if (published)
        {
            AdapterDiagnostics.PresencePublished.Add(1, AdapterDiagnostics.Tag("state", state));
        }
        else
        {
            // Loud for `offline`: the retained value is now stale and every LWT consumer (R-15/T-04)
            // still believes the vehicle is publishing. Nothing corrects it until the device connects
            // again or the broker's own view of the vehicle changes.
            logger.LogError(
                "Could not publish the retained status={State} for vehicle {VehicleId} inside {Window}",
                state, vehicleId, _options.OfflineWindow);
        }

        return published;
    }
}
