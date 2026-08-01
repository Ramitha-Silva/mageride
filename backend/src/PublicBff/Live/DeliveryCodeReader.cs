using MageRide.Shared.Caching;
using StackExchange.Redis;

namespace MageRide.PublicBff.Live;

/// <summary>
/// The four digits SCR-WT-002 shows the recipient to read out to the driver (US-20.5, P-07).
/// </summary>
/// <remarks>
/// <para>
/// <b>It cannot be read out of <c>rides.rides</c>, and that is not an oversight.</b> The column is
/// <c>delivery_otp_hash</c>: ride-svc mints the plaintext at the moment of pickup, rotates the
/// digest in the same statement that takes the pickup gate, and keeps nothing — "it exists in the
/// clear for one hop instead of for the whole booking" (C037). The one hop is
/// <c>package.picked_up</c>, and notification-svc is what is listening.
/// </para>
/// <para>
/// <b>notification-svc is also the component that decided this page would carry it.</b> Its
/// <c>PackagePickedUpAsync</c> sends the code by FCM to a recipient who has the app and deliberately
/// leaves it out of the SMS for one who does not, because D6' I-23.3 has the web page show it "post
/// token validation" — so the token is what carries it and the message body does not. That made the
/// unregistered recipient, who is the entire audience of SCR-WT-002, the one party with no way to
/// learn their own code. Δ C066 closes it: notification-svc writes the plaintext to
/// <see cref="RedisKeys.PackageDeliveryCode"/> in the same handler that mints the
/// <c>package_recipient</c> token, and this reads it back for the holder of that token and for
/// nobody else.
/// </para>
/// <para>
/// <b>Redis rather than a column, and it is the weaker place on purpose.</b> The code is a
/// short-lived credential for one handover: it expires with the delivery window whether or not
/// anything remembers to clear it, it never reaches a backup, and a PDPA erasure has nothing to
/// reach. A <c>delivery_otp_plain</c> column would be the opposite of every one of those.
/// </para>
/// </remarks>
public interface IDeliveryCodeReader
{
    /// <summary>The code, or <see langword="null"/> when the parcel is not aboard yet or it has aged out.</summary>
    Task<string?> ReadAsync(Guid rideId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDeliveryCodeReader"/>
internal sealed class DeliveryCodeReader(IConnectionMultiplexer redis) : IDeliveryCodeReader
{
    public async Task<string?> ReadAsync(Guid rideId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var value = await redis.GetDatabase().StringGetAsync(RedisKeys.PackageDeliveryCode(rideId));

        return value.IsNullOrEmpty ? null : value.ToString();
    }
}
