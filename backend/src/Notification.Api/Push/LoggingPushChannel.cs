namespace MageRide.Notification.Push;

/// <summary>
/// The dev transport: writes the push to the log instead of paying Google and Apple for it.
/// </summary>
/// <remarks>
/// <para>
/// The same shape — and the same guard rail — as iam-svc's <c>DevLoggingOtpSender</c>. It is what
/// makes the dev stack and the walking skeleton runnable without an FCM project and an APNs auth
/// key, and it is what lets a test assert that the right message went to the right handset without
/// a network. <c>NotificationApplication</c> refuses to start with it outside Development unless
/// <c>Notification:AllowLogTransportOutsideDevelopment</c> says otherwise.
/// </para>
/// <para>
/// It answers for both platforms, because the point is to stand in for whichever one is missing.
/// </para>
/// </remarks>
public sealed class LoggingPushChannel(ILogger<LoggingPushChannel> logger) : IPushChannel
{
    public string Platform => DevicePlatforms.Any;

    public string Provider => "log";

    public bool IsConfigured => true;

    public Task<PushResult> SendAsync(DeviceToken device, PushMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(message);

        // Information, with the body in clear: the whole point of this transport.
        logger.LogInformation(
            "[dev-push] {Platform} device {DeviceId} ← {Priority}{Silent} \"{Title}\" / \"{Body}\" data={Data}",
            device.Platform,
            device.Id,
            message.Priority,
            message.Silent ? " silent" : string.Empty,
            message.Title,
            message.Body,
            string.Join(",", message.Data.Select(pair => $"{pair.Key}={pair.Value}")));

        return Task.FromResult(PushResult.Ok(Provider, Guid.NewGuid().ToString("n")));
    }
}
