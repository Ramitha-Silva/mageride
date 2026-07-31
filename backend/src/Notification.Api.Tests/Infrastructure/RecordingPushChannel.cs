using System.Collections.Concurrent;
using MageRide.Notification.Push;

namespace MageRide.Notification.Tests.Infrastructure;

/// <summary>One push a transport was handed.</summary>
internal sealed record SentPush(
    Guid DeviceId, string Platform, string? Title, string? Body, IReadOnlyDictionary<string, string> Data,
    string Priority, bool Silent);

/// <summary>
/// FCM and APNs, as far as this service can tell.
/// </summary>
/// <remarks>
/// <para>
/// <b>A recorder rather than an HTTP stub, unlike the SMS gateways, and the asymmetry is deliberate.</b>
/// The SMS claims are about two sockets racing (D-33), so they need sockets. The push claims are
/// about *what was sent* — high priority, silent, the deep link, the language — and standing up a
/// fake FCM would add an OAuth2 exchange and a service-account key to assert a JSON body this
/// records directly.
/// </para>
/// <para>
/// Registered from the harness's <c>configure</c> hook, which runs before
/// <c>AddNotificationServices</c>, so this channel is first in the <c>IEnumerable&lt;IPushChannel&gt;</c>
/// and <c>NotificationDeliverer.ChannelFor</c> picks it ahead of the log transport.
/// </para>
/// </remarks>
internal sealed class RecordingPushChannel : IPushChannel
{
    private readonly ConcurrentQueue<SentPush> _sent = new();

    public string Platform => DevicePlatforms.Any;

    public string Provider => "recording";

    public bool IsConfigured => true;

    /// <summary>When set, every push is refused — the transient-failure path.</summary>
    public bool Refuse { get; set; }

    /// <summary>When set, every token is reported dead — the FCM <c>UNREGISTERED</c> path.</summary>
    public bool TokensAreDead { get; set; }

    /// <summary>Everything handed to a handset, in order.</summary>
    public IReadOnlyList<SentPush> Sent => [.. _sent];

    public Task<PushResult> SendAsync(DeviceToken device, PushMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(message);

        if (TokensAreDead)
        {
            return Task.FromResult(PushResult.Dead(Provider, "UNREGISTERED"));
        }

        if (Refuse)
        {
            return Task.FromResult(PushResult.Failed(Provider, "the transport refused it"));
        }

        _sent.Enqueue(new SentPush(
            device.Id, device.Platform, message.Title, message.Body, message.Data, message.Priority, message.Silent));

        return Task.FromResult(PushResult.Ok(Provider, Guid.NewGuid().ToString("n")));
    }
}
