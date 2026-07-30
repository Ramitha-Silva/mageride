using System.Buffers;
using MageRide.Shared.Mqtt;
using MageRide.TcpAdapter.Configuration;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Exceptions;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace MageRide.TcpAdapter.Publishing;

/// <summary>
/// The adapter's single connection to EMQX, held across reconnects (ADD §7.7.2's "authenticated
/// bridge user").
/// </summary>
/// <remarks>
/// <para>
/// <b>One connection for every device on the pod, not one per device.</b> The adapter authenticates
/// to the broker as <c>svc-tcp-adapter</c> and <c>acl.conf</c> grants that principal <c>veh/#</c> —
/// so ten thousand tracker sockets share one MQTT session. Minting a per-vehicle session token and
/// opening a broker connection per device would multiply EMQX's connection count by the tracker
/// population for no gain in authorisation: the ACL entry that lets the adapter publish for a vehicle
/// is the <c>svc-</c> one either way.
/// </para>
/// <para>
/// <b>Which means the vehicle binding is this service's to get right.</b> For an MQTT-native device
/// EMQX enforces it — <c>verify_claims</c> ties the username to the token's <c>vehicleId</c> and the
/// ACL confines the device to its own topics. For a tracker on 5023-5026 there is no such
/// enforcement available, because the device cannot present a JWT; the guarantee is instead that the
/// only thing that ever produces a topic here is
/// <see cref="Identity.TrackerAuthorisation.VehicleId"/>, which came from
/// <c>prov.tracker_bindings</c>. That is why nothing in this class takes a topic string from a caller
/// that did not build it from an authorisation.
/// </para>
/// <para>
/// <b>Publishing when the broker is down loses the sample, and that is correct.</b> There is no
/// queue: a position is worth having because it is current, and a tracker sends another in seconds.
/// The one publish worth a deadline is T-04's retained <c>offline</c>, and its window is
/// <c>Adapter:OfflineWindow</c> rather than a retry.
/// </para>
/// </remarks>
public sealed class EmqxLink : BackgroundService
{
    private readonly MqttOptions _mqtt;
    private readonly AdapterOptions _adapter;
    private readonly MqttSessionTokenIssuer _tokens;
    private readonly ILogger<EmqxLink> _logger;
    private readonly List<string> _filters = [];

    private volatile IMqttClient? _client;
    private volatile TaskCompletionSource _connected =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public EmqxLink(
        IOptions<MqttOptions> mqttOptions,
        IOptions<AdapterOptions> adapterOptions,
        MqttSessionTokenIssuer tokens,
        ILogger<EmqxLink> logger)
    {
        ArgumentNullException.ThrowIfNull(mqttOptions);
        ArgumentNullException.ThrowIfNull(adapterOptions);

        _mqtt = mqttOptions.Value;
        _adapter = adapterOptions.Value;
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ClientId = $"{_adapter.ServiceName}-{Guid.NewGuid():N}";
    }

    /// <summary>This pod's MQTT client id. Unique per process: two clients on one id fight.</summary>
    public string ClientId { get; }

    /// <summary>Whether the broker connection is up.</summary>
    public bool IsConnected => _client is { IsConnected: true };

    /// <summary>
    /// What to do with a message on a subscribed filter. Set once, by
    /// <see cref="DownlinkRouter"/>.
    /// </summary>
    public Func<string, ReadOnlyMemory<byte>, Task>? OnMessage { get; set; }

    /// <summary>Adds a filter to subscribe on every (re)connect. Called before the host starts.</summary>
    public void Subscribe(string filter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filter);

        _filters.Add(filter);
    }

    /// <summary>Waits for the connection, so a caller that needs one does not have to poll.</summary>
    public async Task<bool> WaitForConnectionAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (IsConnected)
        {
            return true;
        }

        try
        {
            await _connected.Task.WaitAsync(timeout, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Publishes one message at QoS 1.
    /// </summary>
    /// <returns><see langword="false"/> when there is no connection or the broker refused it.</returns>
    public async Task<bool> PublishAsync(
        string topic, byte[] payload, bool retain, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        var client = _client;

        if (client is null || !client.IsConnected)
        {
            return false;
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            // QoS 1 throughout the plane (D6' §3.1). QoS 0 would drop a sample on a broker restart
            // and QoS 2 doubles the round trips for a delivery guarantee the seq dedupe already gives.
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_adapter.ConnectTimeout);

            var result = await client.PublishAsync(message, timeout.Token);

            if (result.IsSuccess)
            {
                return true;
            }

            // The reason code is the useful part: NotAuthorized means the ACL or the `svc-` prefix is
            // wrong, which is a deployment fault that would otherwise look like silent data loss.
            _logger.LogError(
                "EMQX refused a publish to {Topic}: {Reason}", topic, result.ReasonCode);

            return false;
        }
        catch (Exception exception) when (exception is MqttCommunicationException or OperationCanceledException
                                             && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Publishing to {Topic} failed; the sample is dropped", topic);
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_adapter.BrokerEnabled)
        {
            _logger.LogWarning(
                "Adapter:BrokerEnabled is off — decoded samples are counted and never published. " +
                "This is a test-only configuration.");

            return;
        }

        await Task.Yield();

        var attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(stoppingToken);
                attempt = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                attempt++;
                _logger.LogError(exception, "The EMQX connection ended (attempt {Attempt}); reconnecting", attempt);
            }

            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await Task.Delay(BackoffFor(attempt), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunSessionAsync(CancellationToken stoppingToken)
    {
        using var client = new MqttClientFactory().CreateMqttClient();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        client.DisconnectedAsync += args =>
        {
            _logger.LogWarning(
                "The adapter's EMQX connection dropped: {Reason}", args.ReasonString ?? args.Reason.ToString());

            disconnected.TrySetResult();
            return Task.CompletedTask;
        };

        client.ApplicationMessageReceivedAsync += async args =>
        {
            var handler = OnMessage;

            if (handler is null)
            {
                return;
            }

            // Copied on the receive loop: MQTTnet reuses its packet buffer as soon as this returns
            // and the downlink translation does not run synchronously (C038 learned the same thing).
            var topic = args.ApplicationMessage.Topic;
            var payload = BuffersExtensions.ToArray(args.ApplicationMessage.Payload);

            await handler(topic, payload);
        };

        // The bridge principal: `acl.conf` grants `veh/#` to `^svc-` and to nothing else, and
        // IssueForService is what adds the prefix so a caller cannot mint a service token under a
        // vehicle-shaped username.
        var credential = _tokens.IssueForService(_adapter.ServiceName);

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_mqtt.Host, _mqtt.Port)
            .WithClientId(ClientId)
            .WithCredentials(credential.Username, credential.Jwt)
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithKeepAlivePeriod(_adapter.KeepAlive)
            .WithTimeout(_adapter.ConnectTimeout)
            // No session to resume: everything this process holds is a socket, and a socket does not
            // survive the process. Ending the session promptly also releases the client id.
            .WithCleanStart(true)
            .WithSessionExpiryInterval(0);

        if (_mqtt.UseTls)
        {
            options = options.WithTlsOptions(tls => tls.UseTls(true).WithTargetHost(_mqtt.Host));
        }

        using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        connectCancellation.CancelAfter(_adapter.ConnectTimeout);

        var result = await client.ConnectAsync(options.Build(), connectCancellation.Token);

        if (result.ResultCode != MqttClientConnectResultCode.Success)
        {
            throw new InvalidOperationException(
                $"EMQX refused the adapter's CONNECT: {result.ResultCode} ({result.ReasonString}). " +
                "Check that Mqtt:SessionTokenSecret matches EMQX_AUTHENTICATION__1__SECRET.");
        }

        foreach (var filter in _filters)
        {
            var subscription = await client.SubscribeAsync(
                new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(topic => topic
                        .WithTopic(filter)
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                    .Build(),
                stoppingToken);

            foreach (var item in subscription.Items)
            {
                // A refused subscription is the failure that looks like success from outside: the
                // adapter stays connected and simply never receives a command.
                if (item.ResultCode is not (MqttClientSubscribeResultCode.GrantedQoS0
                    or MqttClientSubscribeResultCode.GrantedQoS1
                    or MqttClientSubscribeResultCode.GrantedQoS2))
                {
                    throw new InvalidOperationException(
                        $"EMQX refused the adapter's subscription to '{filter}': {item.ResultCode}.");
                }
            }
        }

        _client = client;
        _connected.TrySetResult();

        _logger.LogInformation(
            "tcp-adapter connected to EMQX as {Username} ({ClientId}), holding {Filters} filter(s)",
            credential.Username, ClientId, _filters.Count);

        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using (stoppingToken.Register(() => stopped.TrySetResult()))
        {
            await Task.WhenAny(disconnected.Task, stopped.Task);
        }

        _client = null;
        _connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        if (stoppingToken.IsCancellationRequested && client.IsConnected)
        {
            try
            {
                await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "The adapter could not send a clean DISCONNECT to EMQX");
            }
        }
    }

    /// <summary>R-09's jittered exponential backoff, the same symmetric band the bridge uses.</summary>
    private TimeSpan BackoffFor(int attempt)
    {
        if (attempt <= 0)
        {
            return _adapter.ReconnectDelayMin;
        }

        var exponential = _adapter.ReconnectDelayMin * Math.Pow(2, Math.Min(attempt - 1, 16));
        var capped = exponential > _adapter.ReconnectDelayMax ? _adapter.ReconnectDelayMax : exponential;
        var jitter = 1 + ((Random.Shared.NextDouble() - 0.5) / 2);

        return capped * jitter;
    }
}
