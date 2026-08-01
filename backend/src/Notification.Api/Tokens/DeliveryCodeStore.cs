using MageRide.Notification.Configuration;
using MageRide.Shared.Caching;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MageRide.Notification.Tokens;

/// <summary>
/// Leaves the plaintext delivery code where the recipient's web page can read it back (Δ C066).
/// </summary>
/// <remarks>
/// <para>
/// <b>This closes a gap three correct local decisions left between them.</b> ride-svc mints the
/// code at pickup and keeps only the digest — "it exists in the clear for one hop instead of for the
/// whole booking" (C037) — and that one hop is <c>package.picked_up</c>. This handler is what is
/// listening, and it deliberately does <em>not</em> put the code in the SMS, because D6' I-23.3 has
/// the web page show it "post token validation": the token is what carries it and the message body
/// does not. But public-bff, which serves that page, had nothing to read — so the unregistered
/// recipient, who is the <em>entire</em> audience of SCR-WT-002, was the one party on the platform
/// with no way to learn their own code.
/// </para>
/// <para>
/// <b>Written here rather than anywhere else because this is the only place the plaintext is.</b>
/// The alternative was a <c>delivery_otp_plain</c> column on <c>rides.rides</c>, which would put a
/// live credential for every in-flight parcel in the system of record, in backups, and in reach of
/// every read that touches the ride. The value here expires with the delivery window whether or not
/// anything remembers to clear it.
/// </para>
/// <para>
/// <b>It is not allowed to fail the notification.</b> A recipient who gets their SMS and finds no
/// code on the page can still be delivered to by photo proof (P-10); a recipient who gets no SMS
/// cannot be delivered to at all. So the write is best-effort and loud, and the tracking link goes
/// out either way.
/// </para>
/// </remarks>
public interface IDeliveryCodeStore
{
    Task PutAsync(Guid rideId, string code, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDeliveryCodeStore"/>
internal sealed class DeliveryCodeStore(
    IConnectionMultiplexer redis,
    IOptions<NotificationOptions> options,
    ILogger<DeliveryCodeStore> logger) : IDeliveryCodeStore
{
    private readonly NotificationOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task PutAsync(Guid rideId, string code, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        try
        {
            // The same window the token lives for, and for the same reason: both exist so that one
            // handover can happen, and neither should outlive it.
            await redis.GetDatabase().StringSetAsync(
                RedisKeys.PackageDeliveryCode(rideId), code, _options.PackageRecipientTokenTtl);
        }
        catch (RedisException exception)
        {
            logger.LogError(
                exception,
                "The delivery code for package {RideId} could not be cached; SCR-WT-002 will show the "
                + "recipient no code and the driver falls back to photo proof (P-10).",
                rideId);
        }
    }
}
