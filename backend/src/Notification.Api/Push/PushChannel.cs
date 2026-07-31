using MageRide.Notification.Domain;

namespace MageRide.Notification.Push;

/// <summary>One device this service can reach.</summary>
/// <param name="Platform"><c>android</c> | <c>ios</c> — <c>comms.notification_tokens.platform</c>.</param>
public sealed record DeviceToken(Guid Id, Guid UserId, string Platform, string Token);

/// <summary>What goes to a handset.</summary>
/// <param name="Title">Absent on a silent message (E-01's offer, P-02's location request).</param>
/// <param name="Data">
/// The client payload. Values are strings because FCM refuses anything else in <c>data</c>, and
/// APNs is given the same map so the two platforms cannot drift.
/// </param>
/// <param name="Priority">
/// <c>high</c> ⇒ FCM <c>android.priority=high</c> (bypasses Doze) and APNs
/// <c>apns-priority: 10</c>.
/// </param>
/// <param name="Silent">
/// No alert; APNs <c>content-available: 1</c> so the app is woken to act on <paramref name="Data"/>.
/// </param>
public sealed record PushMessage(
    string? Title, string? Body, IReadOnlyDictionary<string, string> Data, string Priority, bool Silent)
{
    public bool IsHighPriority => string.Equals(Priority, NotificationPriorities.High, StringComparison.Ordinal);
}

/// <summary>What one transport did with one device.</summary>
/// <param name="Delivered">The provider accepted it.</param>
/// <param name="TokenIsDead">
/// The provider says this registration token no longer addresses anything —
/// FCM's <c>UNREGISTERED</c>/<c>INVALID_ARGUMENT</c>, APNs' <c>BadDeviceToken</c>/<c>Unregistered</c>.
/// The row is deleted rather than retried: a dead handle is not a transient failure, and every
/// future offer would fan out to it.
/// </param>
public sealed record PushResult(bool Delivered, string Provider, string? MessageId, string? Error, bool TokenIsDead)
{
    public static PushResult Ok(string provider, string? messageId = null) => new(true, provider, messageId, null, false);

    public static PushResult Failed(string provider, string error) => new(false, provider, null, error, false);

    public static PushResult Dead(string provider, string error) => new(false, provider, null, error, true);
}

/// <summary>A push transport for one platform (D6' §7.4).</summary>
public interface IPushChannel
{
    /// <summary><c>android</c> or <c>ios</c>; <c>*</c> for the log transport, which takes both.</summary>
    string Platform { get; }

    /// <summary>Short transport name, recorded on the row.</summary>
    string Provider { get; }

    /// <summary>True when the transport is configured well enough to try.</summary>
    bool IsConfigured { get; }

    Task<PushResult> SendAsync(DeviceToken device, PushMessage message, CancellationToken cancellationToken);
}

/// <summary>Platform identifiers, matching <c>ck_notification_tokens_platform</c> (migration 1302).</summary>
public static class DevicePlatforms
{
    public const string Android = "android";
    public const string Ios = "ios";

    /// <summary>The log transport's platform: it takes whatever it is given.</summary>
    public const string Any = "*";
}
