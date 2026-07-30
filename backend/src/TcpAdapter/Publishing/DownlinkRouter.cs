using System.Text.Json;
using MageRide.Shared.Mqtt;
using MageRide.TcpAdapter.Configuration;
using MageRide.TcpAdapter.Ingest;
using MageRide.TcpAdapter.Observability;
using MageRide.TcpAdapter.Protocols;
using Microsoft.Extensions.Options;

namespace MageRide.TcpAdapter.Publishing;

/// <summary>
/// The canonical downlink envelope: <c>{cmd, args, expiresAt}</c>
/// (<c>mqtt-topics.md</c> §2.2, ADD §7.7.5).
/// </summary>
/// <param name="Cmd">One of the five commands. Anything else is dropped.</param>
/// <param name="Args">Command arguments, as JSON values of whatever type the sender used.</param>
/// <param name="ExpiresAt">
/// After this the command is not delivered. §7.7.5: "expired commands are not delivered on reconnect" —
/// a <c>pingNow</c> that has been sitting in a broker queue since a device lost coverage is not a
/// request anybody still wants answered.
/// </param>
public sealed record CommandEnvelope(string? Cmd, JsonElement? Args, DateTimeOffset? ExpiresAt);

/// <summary>
/// Subscribes <c>veh/+/cmd</c> and writes each command onto the right device's socket (§7.7.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>The adapter subscribes on behalf of devices that cannot.</b> An MQTT-native tracker holds its own
/// subscription to <c>veh/{vehicleId}/cmd</c> and the broker delivers straight to it; a GT06 has no
/// MQTT stack, so this service holds the subscription for every vehicle on the pod (<c>acl.conf</c>
/// grants <c>veh/#</c> to the <c>svc-</c> principal) and translates each envelope into the protocol's
/// own command frame.
/// </para>
/// <para>
/// <b>A command for a vehicle whose device is on another pod is dropped, not forwarded.</b> There is no
/// inter-pod path and inventing one would mean every adapter replica holding a map of every other's
/// sockets. What makes this rare rather than routine is the sticky-by-IMEI-hash deployment
/// (<see cref="Identity.ImeiShards"/>) — and, because a command's whole value is that it reaches the
/// device now, the honest answer to "the device is elsewhere" is a counter and a log line rather than a
/// queue.
/// </para>
/// <para>
/// <b><c>revokeCredential</c> is honoured by closing the socket.</b> No device frame carries it: the
/// credential is revoked in provisioning-svc and the only thing the adapter can do about a device
/// holding a revoked one is stop serving it. That is the same action <see cref="Identity.RevocationWatcher"/>
/// takes, reached through a different door.
/// </para>
/// </remarks>
public sealed class DownlinkRouter(
    EmqxLink link,
    SessionRegistry registry,
    IOptions<AdapterOptions> options,
    TimeProvider clock,
    ILogger<DownlinkRouter> logger)
{
    private readonly AdapterOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Wires the subscription. Called during composition, before the host starts.</summary>
    public void Attach()
    {
        if (!_options.DownlinkEnabled)
        {
            return;
        }

        // The concrete topic names the vehicle, so the filter is a wildcard and the routing key comes
        // off the topic rather than out of the payload — the same rule the bridge follows for the same
        // reason: the topic is the part the broker authorised.
        link.Subscribe("veh/+/cmd");
        link.OnMessage = HandleAsync;
    }

    /// <summary>Handles one command message. Internal so a test can drive it without a broker.</summary>
    internal async Task HandleAsync(string topic, ReadOnlyMemory<byte> payload)
    {
        if (!MqttTopics.TryParse(topic, out var reference) || reference.Kind != MqttTopicKind.Command)
        {
            logger.LogWarning("Ignoring a downlink message on an unexpected topic '{Topic}'", topic);
            return;
        }

        CommandEnvelope? envelope;

        try
        {
            envelope = JsonSerializer.Deserialize<CommandEnvelope>(payload.Span, CommandJson);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "A command on {Topic} could not be read", topic);
            AdapterDiagnostics.CommandsDropped.Add(1, AdapterDiagnostics.Tag("reason", "unreadable"));
            return;
        }

        if (!TrackerCommands.IsKnown(envelope?.Cmd))
        {
            // The set is closed on purpose. GT06's command payload is an opaque ASCII string, so a
            // pass-through would turn anybody able to publish on the topic into a device-configuration
            // channel.
            logger.LogWarning("Refusing an unknown command '{Command}' on {Topic}", envelope?.Cmd, topic);
            AdapterDiagnostics.CommandsDropped.Add(1, AdapterDiagnostics.Tag("reason", "unknown_command"));
            return;
        }

        var command = envelope!.Cmd!;

        if (envelope.ExpiresAt is { } expiresAt && expiresAt <= clock.GetUtcNow())
        {
            logger.LogInformation(
                "Dropping an expired {Command} for vehicle {VehicleId} (expired {ExpiresAt})",
                command, reference.VehicleId, expiresAt);

            AdapterDiagnostics.CommandsDropped.Add(
                1, AdapterDiagnostics.Tag("reason", "expired"), AdapterDiagnostics.Tag("command", command));

            return;
        }

        var sessions = registry.ForVehicle(reference.VehicleId);

        if (sessions.Count == 0)
        {
            AdapterDiagnostics.CommandsDropped.Add(
                1, AdapterDiagnostics.Tag("reason", "no_session"), AdapterDiagnostics.Tag("command", command));

            logger.LogInformation(
                "No device session on this pod for vehicle {VehicleId}; {Command} not delivered",
                reference.VehicleId, command);

            return;
        }

        var arguments = ReadArguments(envelope.Args);

        foreach (var session in sessions)
        {
            await DeliverAsync(session, command, arguments);
        }
    }

    private async Task DeliverAsync(
        ITrackerSession session, string command, IReadOnlyDictionary<string, string> arguments)
    {
        var commandTag = AdapterDiagnostics.Tag("command", command);
        var familyTag = AdapterDiagnostics.Tag("family", ProtocolFamilies.Name(session.Family));

        if (command == TrackerCommands.RevokeCredential)
        {
            await session.CloseAsync("revokeCredential");

            AdapterDiagnostics.CommandsDelivered.Add(1, commandTag, familyTag);
            logger.LogWarning(
                "Closed IMEI {Imei}'s socket on a revokeCredential command for vehicle {VehicleId}",
                session.Imei, session.VehicleId);

            return;
        }

        byte[]? frame;

        try
        {
            frame = session.Codec.TryBuildCommand(command, arguments, session.Imei, session.NextCommandSerial());
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            // An argument a codec could not turn into a frame. Counted rather than propagated: this
            // runs on the broker's receive loop and a throw there stops every other command with it.
            AdapterDiagnostics.CommandsDropped.Add(
                1, AdapterDiagnostics.Tag("reason", "unbuildable"), commandTag, familyTag);

            logger.LogWarning(
                exception, "Could not build a {Family} {Command} frame for IMEI {Imei}",
                ProtocolFamilies.Name(session.Family), command, session.Imei);

            return;
        }

        if (frame is null)
        {
            // Not every one of §7.7.5's five is expressible on every one of D6' §4.1's four. Counted
            // rather than faked: a device that received a command it does not understand discards it
            // silently, which is indistinguishable from one that acted on it.
            AdapterDiagnostics.CommandsDropped.Add(
                1, AdapterDiagnostics.Tag("reason", "unsupported"), commandTag, familyTag);

            logger.LogWarning(
                "{Family} has no frame for {Command}; it was not delivered to IMEI {Imei}",
                ProtocolFamilies.Name(session.Family), command, session.Imei);

            return;
        }

        if (await session.TryWriteAsync(frame, CancellationToken.None))
        {
            AdapterDiagnostics.CommandsDelivered.Add(1, commandTag, familyTag);

            logger.LogInformation(
                "Wrote {Command} to IMEI {Imei} as {Bytes} bytes of {Family}",
                command, session.Imei, frame.Length, ProtocolFamilies.Name(session.Family));

            return;
        }

        AdapterDiagnostics.CommandsDropped.Add(
            1, AdapterDiagnostics.Tag("reason", "write_failed"), commandTag, familyTag);

        logger.LogWarning("Could not write {Command} to IMEI {Imei}; the socket is gone", command, session.Imei);
    }

    /// <summary>
    /// Flattens the envelope's <c>args</c> into strings.
    /// </summary>
    /// <remarks>
    /// The contract's example writes <c>{"seconds": 1}</c> — a number — and a hand-written command from
    /// an operator's console will write <c>"1"</c>. Both have to work, and a codec that took
    /// <see cref="JsonElement"/> would have to re-handle that per command; the codecs parse from strings
    /// with invariant culture instead, which is also what makes them testable without a JSON document.
    /// </remarks>
    private static IReadOnlyDictionary<string, string> ReadArguments(JsonElement? args)
    {
        if (args is not { ValueKind: JsonValueKind.Object } element)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject())
        {
            arguments[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                _ => property.Value.GetRawText(),
            };
        }

        return arguments;
    }

    /// <summary>
    /// camelCase, case-insensitive — the platform's JSON conventions (D3' §0), with no enum converter.
    /// </summary>
    /// <remarks>
    /// Not <c>MageRideJson.Options</c>: that instance's <c>JsonStringEnumConverter</c> is harmless here
    /// but its <c>DefaultIgnoreCondition</c> is a writer setting and this only reads. Spelled locally so
    /// a change to the HTTP plane's serialisation cannot silently change how a device command parses.
    /// </remarks>
    private static readonly JsonSerializerOptions CommandJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };
}
