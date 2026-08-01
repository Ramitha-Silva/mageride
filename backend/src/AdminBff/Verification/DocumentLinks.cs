using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MageRide.AdminBff.Configuration;
using MageRide.AdminBff.Domain;
using MageRide.AdminBff.Endpoints;
using MageRide.Shared.Storage;
using Microsoft.Extensions.Options;

namespace MageRide.AdminBff.Verification;

/// <summary>
/// The two URLs a document carries on the wire, and the short-lived signed object-storage URL the
/// viewer finally redirects to (AL-39, US-24.8).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every document read goes through the audited route, and that is the fence.</b> AL-39 asks for
/// two things at once — reads use short-lived signed object-storage URLs, <em>and</em> every read
/// emits a <c>DOC_VIEW</c> audit event. Handing the portal a bare pre-signed bucket URL in
/// <c>thumbUrl</c> would satisfy the first and quietly break the second: the officer's browser would
/// fetch somebody's licence straight from storage and nothing would ever record it. So
/// <see cref="Create"/> mints a link to <c>GET /v1/admin/documents/{docId}</c> — RBAC-gated,
/// bearer-carrying, audited — and <see cref="SignedObjectUrl"/> is what that route answers with.
/// The signed URL is where it always was; what changed is that it is minted per view, on the way
/// through the row that records the view.
/// </para>
/// <para>
/// <b>Δ D-36: the signed URL is now the bucket's own.</b> <see cref="SignedObjectUrl"/> asks the
/// kernel's <c>IObjectStore</c> to presign a GET, which is what AL-39 asked for all along — the
/// credential is an AWS SigV4 signature the storage provider verifies, the TTL is enforced by the
/// provider, and no MageRide process carries the bytes.
/// </para>
/// <para>
/// <b>The HMAC path is kept for a deployment with no bucket, and only for that.</b> Where
/// <c>Storage:*</c> is unset the store cannot presign, and the fallback is the same HMAC over the
/// doc id, the rendition, the deadline and the stored object key — the link still expires and still
/// cannot be re-pointed at another document by editing a path segment. Which one a deployment got
/// is visible in the start-up log rather than inferred.
/// </para>
/// <para>
/// <b>No bearer travels in a query string.</b> The redirect target is fetched by an image loader
/// that carries no token, and putting an access token there would put it in every proxy log the
/// request passes through — the same argument subscription-svc's <c>ModeBFileLinks</c> makes.
/// </para>
/// </remarks>
public interface IDocumentLinks
{
    /// <summary>The audited viewer link for one rendition of a document.</summary>
    string Create(Guid docId, string variant);

    /// <summary>
    /// The short-lived signed object-storage URL <c>GET /v1/admin/documents/{docId}</c> redirects
    /// to, and the instant it stops working.
    /// </summary>
    (string Url, DateTimeOffset ExpiresAt) SignedObjectUrl(StoredDocument document, string variant);

    /// <summary>
    /// Whether an HMAC object URL this service minted is still valid — the no-bucket path only.
    /// </summary>
    bool Verify(Guid docId, string variant, string storageUrl, string? expires, string? signature);
}

/// <inheritdoc cref="IDocumentLinks"/>
internal sealed class DocumentLinks : IDocumentLinks
{
    private readonly AdminBffOptions.DocumentOptions _options;
    private readonly IObjectStore _objects;
    private readonly TimeProvider _clock;
    private readonly byte[] _key;

    public DocumentLinks(
        IOptions<AdminBffOptions> options,
        IObjectStore objects,
        TimeProvider clock,
        ILogger<DocumentLinks> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value.Documents;
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _clock = clock ?? TimeProvider.System;

        if (string.IsNullOrWhiteSpace(_options.SigningKey))
        {
            // Correct for one instance and wrong for several: a URL minted by replica A does not
            // verify on replica B, and the officer sees a broken image in a lightbox the server
            // opened a second ago.
            _key = RandomNumberGenerator.GetBytes(32);

            // Harmless once a bucket is configured — nothing signs with this key then — so the
            // warning names the condition under which it actually bites.
            logger.LogWarning(
                "AdminBff:Documents:SigningKey is not configured, so the fallback document URLs are signed with "
                + "a key generated for this process. With no Storage:* bucket configured that means a URL minted "
                + "by one replica does not verify on another, and the officer sees a broken image in a lightbox "
                + "the server opened a second ago.");
        }
        else
        {
            _key = Encoding.UTF8.GetBytes(_options.SigningKey);
        }
    }

    public string Create(Guid docId, string variant)
    {
        RequireKnownVariant(variant);

        return $"{AdminEndpoints.Prefix}/documents/{docId:D}?variant={variant}";
    }

    public (string Url, DateTimeOffset ExpiresAt) SignedObjectUrl(StoredDocument document, string variant)
    {
        ArgumentNullException.ThrowIfNull(document);
        RequireKnownVariant(variant);

        var expiresAt = _clock.GetUtcNow() + _options.UrlTtl;

        // D-36's bucket signs its own reads. Preferred over the HMAC below because the provider
        // enforces the deadline and the object never passes through a MageRide process.
        if (_objects.TryPresign(document.StorageUrl, _options.UrlTtl, out var presigned))
        {
            return (presigned, expiresAt);
        }

        var expires = expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var target = Resolve(document.StorageUrl);
        var separator = target.Contains('?', StringComparison.Ordinal) ? '&' : '?';

        return (
            $"{target}{separator}variant={variant}&expires={expires}"
            + $"&signature={Sign(document.DocId, variant, expires, document.StorageUrl)}",
            expiresAt);
    }

    public bool Verify(Guid docId, string variant, string storageUrl, string? expires, string? signature)
    {
        if (!DocumentVariants.IsKnown(variant)
            || string.IsNullOrWhiteSpace(expires)
            || string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        if (!long.TryParse(expires, NumberStyles.None, CultureInfo.InvariantCulture, out var deadline)
            || DateTimeOffset.FromUnixTimeSeconds(deadline) <= _clock.GetUtcNow())
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

        return CryptographicOperations.FixedTimeEquals(
            presented, Convert.FromHexString(Sign(docId, variant, expires, storageUrl)));
    }

    /// <summary>
    /// The stored pointer as something a browser can follow.
    /// </summary>
    /// <remarks>
    /// <c>registry.documents.file_url</c> holds whatever the uploading service wrote — an absolute
    /// URL where object storage exists, a filesystem path under fleet-svc's <c>DocumentRoot</c>
    /// where it does not. <c>PublicBaseUrl</c> is what turns the second into the first; unset, the
    /// stored value is passed through unchanged and announced at start-up, because inventing a host
    /// would produce a link that 404s somewhere nobody is looking.
    /// </remarks>
    private string Resolve(string storageUrl)
    {
        if (string.IsNullOrWhiteSpace(_options.PublicBaseUrl)
            || Uri.TryCreate(storageUrl, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            return storageUrl;
        }

        return $"{_options.PublicBaseUrl.TrimEnd('/')}/{storageUrl.TrimStart('/')}";
    }

    /// <remarks>
    /// The object key is inside the signature as well as the doc id: the two are bound together, so
    /// a link cannot be re-pointed at a different object by editing the path, and a document whose
    /// bytes are replaced does not keep an old link alive.
    /// </remarks>
    private string Sign(Guid docId, string variant, string expires, string storageUrl) =>
        Convert.ToHexString(HMACSHA256.HashData(
            _key, Encoding.UTF8.GetBytes($"{docId:D}\n{variant}\n{expires}\n{storageUrl}"))).ToLowerInvariant();

    private static void RequireKnownVariant(string variant)
    {
        if (!DocumentVariants.IsKnown(variant))
        {
            throw new ArgumentOutOfRangeException(
                nameof(variant), variant, $"variant must be {DocumentVariants.Thumb} or {DocumentVariants.Full}.");
        }
    }
}
