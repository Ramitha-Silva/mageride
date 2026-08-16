using System.Diagnostics;
using System.Globalization;
using MageRide.HotPath.PositionProcessor.Configuration;
using MageRide.Security.Tests.AntiSpoof.Corpus;
using MageRide.Shared.Mqtt;
using MageRide.TestKit;

namespace MageRide.Security.Tests.AntiSpoof.Mqtt;

/// <summary>
/// D-17's publish ceiling, on the broker that enforces it (D5' §5.3, ADD §12.6,
/// <c>mqtt-topics.md</c> §4).
///
/// <para>
/// There are three lines and they are not the same control. EMQX's <c>messages_rate = "5/s"</c> is
/// per <b>connection</b> and <b>paces</b>; mqtt-bridge-svc's is per <b>vehicle</b> and only
/// reports; position-processor-svc's is per vehicle at twice the rate and <b>drops</b>. This class
/// measures the first and asserts the gap between it and the other two — because that gap is the
/// entire reason the other two exist, and it is invisible from any single one of them.
/// </para>
/// </summary>
[Collection<AntiSpoofCollection>]
[Trait("Category", "AntiSpoof")]
public sealed class PublishCeilingTests(EmqxFixture emqx)
{
    /// <summary>
    /// D-17's ceiling, per connection, as <c>emqx.conf</c> sets it on every listener a DEVICE can
    /// reach — 8883 (trackers) and 8084 (mobile).
    /// </summary>
    private const int CeilingPerSecond = 5;

    /// <summary>
    /// A single connection cannot outrun the ceiling however hard it tries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Paced, not dropped</b> — and the difference matters to what a caller can conclude. EMQX's
    /// listener limiter delays a publisher that exceeds its rate rather than refusing the message,
    /// so a flooding device is slowed rather than told; a test that looked for a refusal would find
    /// none and wrongly report the ceiling as absent.
    /// </para>
    /// <para>
    /// The assertion is a lower bound on elapsed time with generous slack, because a token bucket
    /// has a burst allowance and the exact size of it is EMQX's business. What it rules out is the
    /// case that matters: the ceiling not being applied at all, which would let this finish in
    /// milliseconds.
    /// </para>
    /// <para>
    /// <b><see cref="MqttPlane.InClusterTcp"/> is not in this theory, and its absence is the
    /// assertion.</b> It was, until 2026-08-14, and 1883's 5 msg/s was the ~10 msg/s ingest ceiling
    /// C129 measured against a 1,200 msg/s launch target: no device reaches 1883, mqtt-bridge-svc
    /// holds E-08's shared subscription there for the whole fleet, and a per-connection limit on
    /// that listener is a per-FLEET limit on ingest. D-17 is a per-vehicle ceiling and is proved
    /// here on the plane a driver's handset actually uses.
    /// <see cref="BrokerPolicyTests.Every_device_listener_carries_the_five_messages_a_second_ceiling"/>
    /// is what keeps 1883 from silently acquiring one again — it asserts the service listener has a
    /// ceiling AND that it is not D-17's.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(MqttPlane.MobileWebSocket)]
    public async Task One_connection_cannot_publish_faster_than_the_configured_ceiling(MqttPlane plane)
    {
        RequireBroker();

        const int messages = 40;

        var vehicleId = Guid.NewGuid();
        await using var device = await MqttDevice.ConnectAsync(emqx, vehicleId, plane);

        var topic = MqttTopics.PositionLive(vehicleId);
        var elapsed = Stopwatch.StartNew();

        for (var i = 0; i < messages; i++)
        {
            var result = await device.PublishAsync(topic, "{}"u8.ToArray());
            Assert.True(result.IsSuccess, $"{plane}: publish {i} was refused rather than paced.");
        }

        elapsed.Stop();

        // Half the theoretical floor, so a burst allowance of up to half the run cannot fail this.
        var floor = TimeSpan.FromSeconds(messages / (double)CeilingPerSecond / 2d);

        Assert.True(
            elapsed.Elapsed >= floor,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{plane}: {messages} publishes on one connection took {elapsed.Elapsed.TotalSeconds:F2} s. At D-17's {CeilingPerSecond} msg/s ceiling they cannot take less than {floor.TotalSeconds:F2} s even allowing a full burst, so `messages_rate` is not being applied on this listener."));

        Assert.True(device.IsConnected, "the broker paces a fast publisher; it does not disconnect one");
    }

    /// <summary>
    /// The gap the broker cannot close: the listener limit is per connection, and D-17's is per
    /// vehicle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One credential, several sessions. <c>mqtt-topics.md</c> §4 names this as the case the first
    /// line cannot see, and it is why mqtt-bridge-svc keys its counter on <c>vehicleId</c> in Redis
    /// and why position-processor-svc's second line drops rather than reports. Proving it here is
    /// what turns those two components from belt-and-braces into the control.
    /// </para>
    /// <para>
    /// <b>This is a demonstration of a known limit, not a failure.</b> Nothing in D6' §3 asks EMQX
    /// to enforce a per-vehicle rate — a listener limiter cannot, because it sees a socket rather
    /// than a principal — and the platform's answer is the two server-side lines behind it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Several_sessions_under_one_credential_beat_the_per_connection_ceiling()
    {
        RequireBroker();

        const int connections = 4;
        const int perConnection = 15;

        var vehicleId = Guid.NewGuid();
        var topic = MqttTopics.PositionLive(vehicleId);

        var devices = await Task.WhenAll(Enumerable.Range(0, connections)
            .Select(_ => MqttDevice.ConnectAsync(emqx, vehicleId, MqttPlane.InClusterTcp)));

        try
        {
            var elapsed = Stopwatch.StartNew();

            await Task.WhenAll(devices.Select(async device =>
            {
                for (var i = 0; i < perConnection; i++)
                {
                    await device.PublishAsync(topic, "{}"u8.ToArray());
                }
            }));

            elapsed.Stop();

            var achieved = connections * perConnection / elapsed.Elapsed.TotalSeconds;

            Assert.True(
                achieved > CeilingPerSecond,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Four sessions under one vehicle credential achieved {achieved:F1} msg/s, which is not above the {CeilingPerSecond} msg/s per-connection ceiling. If the broker has gained a per-principal limiter, that is an improvement — but then D-17's second and third lines are no longer the only per-vehicle control and their comments should say so."));
        }
        finally
        {
            foreach (var device in devices)
            {
                await device.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// The two server-side lines are configured at the rates the specs give, and in the right order.
    /// </summary>
    /// <remarks>
    /// The ordering is the assertion that matters: the processor's line has to be <i>above</i> the
    /// broker's, or a vehicle behaving exactly as the platform told it to would be dropped by the
    /// backstop. D5' §5.2's near-geofence cadence is one sample a second and R-07 lets the server
    /// ask for bursts.
    /// </remarks>
    [Fact]
    public void The_two_server_side_lines_sit_above_the_brokers_and_below_each_other()
    {
        var processor = PositionCorpus.Deployed;
        var bridge = int.Parse(
            DeployedConfiguration.Current["MqttBridge:PublishCeilingPerSecond"]
            ?? throw new InvalidOperationException("MqttBridge__PublishCeilingPerSecond is not deployed."),
            CultureInfo.InvariantCulture);

        // mqtt-bridge observes D-17's number itself: same ceiling, per vehicle rather than per
        // connection, and it reports rather than drops because a position dropped there is one
        // anti-spoof never gets to look at.
        Assert.Equal(CeilingPerSecond, bridge);

        // position-processor drops, at twice the rate, averaged over ten seconds (D5' §5.3).
        Assert.Equal(10, processor.RateCeilingPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(10), processor.RateCheckWindow);

        Assert.True(
            processor.RateCeilingPerSecond > bridge,
            "The dropping line must sit above the reporting one, or a vehicle publishing at exactly "
            + "the ceiling the platform asked it for would lose samples at the backstop.");
    }

    /// <summary>
    /// T-05's backlog rate is a separate, higher limit — and it is not the broker's.
    /// </summary>
    /// <remarks>
    /// 20 samples/s/device on <c>pos/replay</c> (D5' §5.3, §13.3), which is four times what the
    /// broker will let one connection push. So the pacer that enforces it is mqtt-bridge-svc's, and
    /// a reader who assumed the broker did it would be looking at a control that cannot reach the
    /// number.
    /// </remarks>
    [Fact]
    public void The_backlog_rate_is_above_what_one_broker_connection_can_deliver()
    {
        var replay = int.Parse(
            DeployedConfiguration.Current["MqttBridge:ReplaySamplesPerSecond"]
            ?? throw new InvalidOperationException("MqttBridge__ReplaySamplesPerSecond is not deployed."),
            CultureInfo.InvariantCulture);

        Assert.Equal(20, replay);
        Assert.True(replay > CeilingPerSecond * 2);
    }

    private void RequireBroker() => Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);
}
