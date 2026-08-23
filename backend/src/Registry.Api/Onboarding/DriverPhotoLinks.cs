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
    /// <para>
    /// Relative, like <c>TicketDetail.screenshotUrl</c>: this service does not know the origin it
    /// is reached on, and an app that resolves against its own configured gateway cannot be sent
    /// somewhere else by a response.
    /// </para>
    /// <para>
    /// <b>The URL names the PHOTO, not just the driver.</b> <paramref name="storageUrl"/> is folded
    /// into a short opaque <c>v</c>, so replacing a photo changes the link. Without that the URL is
    /// stable for the life of the account and every cache in the path — the loader's, the OS's, a
    /// CDN's — goes on serving the picture the driver just replaced. It is also what lets a
    /// superseded link stop working, which a stable one could not.
    /// </para>
    /// </remarks>
    string Create(Guid driverId, string storageUrl);

    /// <summary>
    /// Whether a presented signature is one this service issued, has not expired, and names the
    /// photo the driver currently has.
    /// </summary>
    bool Verify(Guid driverId, string currentStorageUrl, string? version, string? expires, string? signature);

    /// <summary>The relative, signed URL for one of a driver's documents (Δ MCS-28).</summary>
    /// <remarks>
    /// <b>The signature names the driver AND the document.</b> The route re-checks entitlement
    /// against <c>driver_eligible_vehicles</c> anyway, so this is the second of two locks rather
    /// than the only one — a signing key that ever leaked would otherwise be a key to every NIC and
    /// driving licence on the platform, which is a materially worse thing to hold than an avatar.
    /// </remarks>
    string CreateDocument(Guid driverId, Guid documentId, string storageUrl);

    /// <summary>Whether a presented document signature is one this service issued and still current.</summary>
    bool VerifyDocument(
        Guid driverId, Guid documentId, string currentStorageUrl, string? version, string? expires, string? signature);
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

    /// <summary>
    /// The document link's own domain separation (Δ MCS-28).
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Purpose"/> so a signature minted for an avatar can never verify as
    /// one for a driving licence, whatever else goes wrong. The two routes read different columns
    /// and answer different bytes; sharing a signed message between them would be one refactor away
    /// from being the same link.
    /// </remarks>
    private const string DocumentPurpose = "driver_document_image";

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

    public string Create(Guid driverId, string storageUrl)
    {
        var expires = (_clock.GetUtcNow() + _options.ProfilePhotoLinkTtl).ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);

        var version = Version(storageUrl);

        return $"/v1/drivers/{driverId}/profile-photo"
            + $"?v={version}&expires={expires}&signature={Sign(driverId, version, expires)}";
    }

    public bool Verify(Guid driverId, string currentStorageUrl, string? version, string? expires, string? signature) =>
        Verify(
            signed: Sign(driverId, version ?? string.Empty, expires ?? string.Empty),
            currentStorageUrl: currentStorageUrl,
            version: version,
            expires: expires,
            signature: signature);

    /// <summary>
    /// Everything both links check, given the signature the caller should have presented.
    /// </summary>
    /// <remarks>
    /// One body rather than two, because these are the properties the whole arrangement rests on —
    /// the deadline is real, the version names what the driver has now, and the comparison is
    /// fixed-time. A second copy is a second place for one of the three to go missing.
    /// </remarks>
    private bool Verify(
        string signed, string currentStorageUrl, string? version, string? expires, string? signature)
    {
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(expires)
            || string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        // The link must name the photo the driver has NOW. A link minted for a photo since replaced
        // is refused rather than quietly serving the new one — otherwise "here is my avatar" would
        // be a URL that outlives every picture it was ever issued for.
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(version), Encoding.UTF8.GetBytes(Version(currentStorageUrl))))
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
        return CryptographicOperations.FixedTimeEquals(presented, Convert.FromHexString(signed));
    }

    public string CreateDocument(Guid driverId, Guid documentId, string storageUrl)
    {
        var expires = (_clock.GetUtcNow() + _options.ProfilePhotoLinkTtl).ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);

        var version = Version(storageUrl);

        return $"/v1/drivers/documents/{documentId}/image"
            + $"?d={driverId}&v={version}&expires={expires}"
            + $"&signature={SignDocument(driverId, documentId, version, expires)}";
    }

    public bool VerifyDocument(
        Guid driverId, Guid documentId, string currentStorageUrl, string? version, string? expires, string? signature) =>
        Verify(
            signed: SignDocument(driverId, documentId, version ?? string.Empty, expires ?? string.Empty),
            currentStorageUrl: currentStorageUrl,
            version: version,
            expires: expires,
            signature: signature);

    private string SignDocument(Guid driverId, Guid documentId, string version, string expires) =>
        Convert.ToHexStringLower(
            HMACSHA256.HashData(
                _key, Encoding.UTF8.GetBytes($"{DocumentPurpose}|{driverId}|{documentId}|{version}|{expires}")));

    private string Sign(Guid driverId, string version, string expires) =>
        Convert.ToHexStringLower(
            HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{Purpose}|{driverId}|{version}|{expires}")));

    /// <summary>
    /// A short opaque stand-in for which photo this is.
    /// </summary>
    /// <remarks>
    /// Keyed rather than a bare digest, so it reveals nothing about the object key it is derived
    /// from; eight hex characters, because it only has to distinguish one driver's successive
    /// photographs from each other and it is going in a URL a person may read out.
    /// </remarks>
    private string Version(string storageUrl) =>
        Convert.ToHexStringLower(
            HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{Purpose}|v|{storageUrl}")))[..8];
}
