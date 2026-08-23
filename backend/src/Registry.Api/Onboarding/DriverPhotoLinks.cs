using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MageRide.Registry.Configuration;
using Microsoft.Extensions.Options;

namespace MageRide.Registry.Onboarding;

/// <summary>
/// Signs and verifies the expiring URL the driver profile reads carry in place of <c>photo_url</c>
/// (Δ MCS-25).
/// </summary>
/// <remarks>
/// <para>
/// <b>What the column holds is not something a client can fetch.</b>
/// <see cref="OnboardingService"/> stores <c>photo.StorageUrl</c>, which
/// <see cref="MageRide.Shared.Storage.StoredObject"/> defines as <c>s3://bucket/key</c> or
/// <c>file://…</c> — a pointer this service resolves, not a URL. Handing it to an app put a scheme
/// no image loader understands into a field the contract calls <c>format: uri</c>, so both driver
/// apps drew a placeholder glyph and the photograph a driver had been required to upload
/// (AL-27) was never shown back to them.
/// </para>
/// <para>
/// <b>This is support-svc's screenshot link, for the same reasons and with one difference.</b>
/// The properties that make it work are that one's: the link is unguessable, it expires, and
/// following it needs no bearer token — which is what lets a header put it straight into an image
/// view. An access token would be the wrong credential even where it was convenient, because an
/// image loader does not carry one and a token in a query string is a token in every proxy log on
/// the way.
/// </para>
/// <para>
/// The difference is what the signature names. support-svc signs an upload id and then has to
/// re-check the <c>kind</c>, because <c>docs.uploads</c> also holds driving licences and bank
/// statements — a leaked key would otherwise be a key to somebody's NIC. This signs the
/// <b>driver id</b>, and the route reads one column: <c>registry.driver_profiles.photo_url</c>.
/// That column can hold nothing but a profile photo, so the narrowing is structural rather than a
/// check that has to be remembered.
/// </para>
/// </remarks>
public interface IDriverPhotoLinks
{
    /// <summary>The relative, signed URL for one driver's profile photo.</summary>
    /// <remarks>
    /// Relative, like <c>TicketDetail.screenshotUrl</c>: this service does not know the origin it
    /// is reached on, and an app that resolves against its own configured gateway cannot be sent
    /// somewhere else by a response.
    /// </remarks>
    string Create(Guid driverId);

    /// <summary>Whether a presented signature is one this service issued and has not expired.</summary>
    bool Verify(Guid driverId, string? expires, string? signature);
}

/// <inheritdoc cref="IDriverPhotoLinks"/>
public sealed class DriverPhotoLinks : IDriverPhotoLinks
{
    /// <summary>
    /// What the HMAC is domain-separated with.
    /// </summary>
    /// <remarks>
    /// The same key could one day sign another kind of registry link. Naming the purpose inside the
    /// signed message means a signature minted for one can never verify for another, which costs a
    /// few bytes now and removes a class of mistake later.
    /// </remarks>
    private const string Purpose = "driver_profile_photo";

    private readonly RegistryOptions _options;
    private readonly TimeProvider _clock;
    private readonly byte[] _key;

    public DriverPhotoLinks(IOptions<RegistryOptions> options, TimeProvider clock, ILogger<DriverPhotoLinks> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _clock = clock ?? TimeProvider.System;

        if (string.IsNullOrWhiteSpace(_options.ProfilePhotoLinkSigningKey))
        {
            // A generated key is correct for one instance and wrong for several: a link minted by
            // replica A does not verify on replica B, and the driver sees a broken avatar on a
            // header the server rendered a second ago.
            _key = RandomNumberGenerator.GetBytes(32);

            logger.LogWarning(
                "Registry:ProfilePhotoLinkSigningKey is not configured, so the driver profile photo URL is signed "
                + "with a key generated for this process. Those links will not verify on another replica or "
                + "survive a restart.");
        }
        else
        {
            _key = Encoding.UTF8.GetBytes(_options.ProfilePhotoLinkSigningKey);
        }
    }

    public string Create(Guid driverId)
    {
        var expires = (_clock.GetUtcNow() + _options.ProfilePhotoLinkTtl).ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);

        return $"/v1/drivers/{driverId}/profile-photo?expires={expires}&signature={Sign(driverId, expires)}";
    }

    public bool Verify(Guid driverId, string? expires, string? signature)
    {
        if (string.IsNullOrWhiteSpace(expires) || string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        if (!long.TryParse(expires, NumberStyles.None, CultureInfo.InvariantCulture, out var expiresUnix)
            || DateTimeOffset.FromUnixTimeSeconds(expiresUnix) <= _clock.GetUtcNow())
        {
            return false;
        }

        byte[] presented;
        try
        {
            presented = Convert.FromHexString(signature);
        }
        catch (FormatException)
        {
            return false;
        }

        // Fixed-time, so a caller cannot learn a valid signature one byte at a time from how long
        // the comparison took.
        return CryptographicOperations.FixedTimeEquals(
            presented, Convert.FromHexString(Sign(driverId, expires)));
    }

    private string Sign(Guid driverId, string expires) =>
        Convert.ToHexStringLower(
            HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{Purpose}|{driverId}|{expires}")));
}
