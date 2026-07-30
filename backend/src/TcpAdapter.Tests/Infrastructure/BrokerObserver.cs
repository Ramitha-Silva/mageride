using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using MageRide.Shared.Mqtt;
using MageRide.Shared.Telemetry;
using MageRide.TestKit;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace MageRide.TcpAdapter.Tests.Infrastructure;

/// <summary>
/// An MQTT client watching what the adapter actually put on the broker.
/// </summary>
/// <remarks>
/// <para>
/// Everything this suite claims about publishing is asserted from here rather than from the adapter's
/// own counters. "A half-closed socket publishes a retained <c>status=offline</c>" is a statement about
/// what a later subscriber reads off <c>veh/{vehicleId}/status</c>, and an adapter that incremented a
/// counter and published nothing would satisfy any assertion made on its own side of the wire.
/// </para>
/// <para>
/// It connects as <c>svc-tests</c>, because <c>acl.conf</c> grants <c>veh/#</c> to the <c>^svc-</c>
/// prefix and to nothing else — the same grant the adapter itself relies on, so a policy change that
/// broke the adapter would break this observer too rather than let a test pass against a broker the
/// service could not use.
/// </para>
/// </remarks>
internal sealed class BrokerObserver : IAsyncDisposable
{
    private readonly IMqttClient _client;
    private readonly ConcurrentQueue<Observed> _messages = new();
    private readonly SemaphoreSlim _arrived = new(0);

    private BrokerObserver(IMqttClient client) => _client = client;

    /// <summary>Connects and subscribes to <paramref name="filters"/>.</summary>
    public static async Task<BrokerObserver> SubscribeAsync(EmqxFixture emqx, params string[] filters)
    {
        ArgumentNullException.ThrowIfNull(emqx);
        emqx.RequireAvailable();

        var client = new MqttClientFactory().CreateMqttClient();
        var observer = new BrokerObserver(client);

        client.ApplicationMessageReceivedAsync += args =>
        {
            observer._messages.Enqueue(new Observed(
                args.ApplicationMessage.Topic,
                BuffersExtensions.ToArray(args.ApplicationMessage.Payload),
                args.ApplicationMessage.Retain));

            observer._arrived.Release();
            return Task.CompletedTask;
        };

        var credential = new MqttSessionTokenIssuer(
                Options.Create(new MqttOptions { SessionTokenSecret = EmqxFixture.SessionTokenSecret }),
                TimeProvider.System)
            .IssueForService("tests");

        var result = await client.ConnectAsync(
            new MqttClientOptionsBuilder()
                .WithTcpServer(emqx.Host, emqx.Port)
                .WithClientId($"observer-{Guid.NewGuid():N}"[..23])
                .WithCredentials(credential.Username, credential.Jwt)
                .WithProtocolVersion(MqttProtocolVersion.V500)
                .WithCleanStart(true)
                .WithSessionExpiryInterval(0)
                .WithTimeout(TimeSpan.FromSeconds(15))
                .Build(),
            CancellationToken.None);

        if (result.ResultCode != MqttClientConnectResultCode.Success)
        {
            await observer.DisposeAsync();

            throw new InvalidOperationException($"EMQX refused the observer's CONNECT: {result.ResultCode}.");
        }

        foreach (var filter in filters)
        {
            await client.SubscribeAsync(
                new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(topic => topic
                        .WithTopic(filter)
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                    .Build(),
                CancellationToken.None);
        }

        return observer;
    }

    /// <summary>Publishes a command envelope onto a vehicle's downlink topic, as any producer would.</summary>
    public Task PublishCommandAsync(Guid vehicleId, string json) =>
        _client.PublishAsync(
            new MqttApplicationMessageBuilder()
                .WithTopic(MqttTopics.Command(vehicleId))
                .WithPayload(Encoding.UTF8.GetBytes(json))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build(),
            CancellationToken.None);

    /// <summary>Waits for the first message matching <paramref name="predicate"/>, or fails.</summary>
    public async Task<Observed> WaitForAsync(
        Func<Observed, bool> predicate, TimeSpan timeout, string because)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        var deadline = DateTime.UtcNow + timeout;
        var seen = new List<Observed>();

        while (DateTime.UtcNow < deadline)
        {
            while (_messages.TryDequeue(out var message))
            {
                if (predicate(message))
                {
                    return message;
                }

                seen.Add(message);
            }

            await _arrived.WaitAsync(TimeSpan.FromMilliseconds(100));
        }

        Assert.Fail(
            $"{because}. Saw {seen.Count} message(s): " +
            string.Join(", ", seen.Select(message => message.Topic).Distinct()));

        throw new InvalidOperationException("unreachable");
    }

    /// <summary>Waits for a position sample on a vehicle's live or replay topic.</summary>
    public async Task<PositionSample> WaitForSampleAsync(
        Guid vehicleId, bool replay = false, TimeSpan? timeout = null)
    {
        var topic = replay ? MqttTopics.PositionReplay(vehicleId) : MqttTopics.PositionLive(vehicleId);

        var message = await WaitForAsync(
            observed => observed.Topic == topic,
            timeout ?? TimeSpan.FromSeconds(20),
            $"no sample arrived on {topic}");

        var sample = PositionSampleCodec.TryDecode(message.Payload);

        Assert.NotNull(sample);
        return sample!;
    }

    /// <summary>Waits for a retained presence message with the expected value (T-04).</summary>
    public Task<Observed> WaitForPresenceAsync(Guid vehicleId, string state, TimeSpan? timeout = null) =>
        WaitForAsync(
            observed => observed.Topic == MqttTopics.Status(vehicleId)
                        && Encoding.UTF8.GetString(observed.Payload) == state,
            timeout ?? TimeSpan.FromSeconds(20),
            $"no status={state} arrived on {MqttTopics.Status(vehicleId)}");

    /// <summary>Whether anything at all has arrived on a topic yet.</summary>
    public bool Saw(Func<Observed, bool> predicate) => _messages.Any(predicate);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_client.IsConnected)
            {
                await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None);
            }
        }
        catch (Exception)
        {
            // A broker that already dropped us.
        }

        _client.Dispose();
        _arrived.Dispose();
    }

    /// <summary>One message off the broker.</summary>
    /// <param name="Topic">The concrete topic — the part EMQX authorised.</param>
    /// <param name="Payload">The bytes.</param>
    /// <param name="Retained">
    /// Whether the broker delivered it as a retained message. Note this is <see langword="false"/> for a
    /// message published <i>with</i> the retain flag while the subscription was already live — MQTT sets
    /// it only on the replay a new subscriber receives.
    /// </param>
    internal sealed record Observed(string Topic, byte[] Payload, bool Retained);
}
