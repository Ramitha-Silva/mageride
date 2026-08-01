using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MageRide.AdminBff.Configuration;
using MageRide.Shared.Storage;
using Microsoft.Extensions.Options;

namespace MageRide.AdminBff.Pdpa;

/// <summary>
/// The short-lived download URL a fulfilled export is handed out under, and the instant it dies.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own type rather than a fourth method on <c>IDocumentLinks</c>, and the difference is not
/// cosmetic.</b> A document link is minted by an <em>audited operator route</em> that <c>302</c>s to
/// the object — one view, one <c>DOC_VIEW</c> row — and is bound to a doc id and a rendition. A PDPA
/// artifact is handed to the <em>data subject</em> in the body of their own status read, has no
/// renditions, and is bound to the request rather than to a document. Sharing one signer would mean
/// a URL minted for one could be replayed against the other, because the HMAC would cover the same
/// fields.
/// </para>
/// <para>
/// <b>The bucket signs it where there is one.</b> With <c>Storage:S3:*</c> configured this is the
/// provider's own SigV4 presigned GET: the deadline is enforced by the storage provider and no
/// MageRide process carries a copy of somebody's entire personal history. The HMAC fallback below
/// exists for a deployment with no bucket and only for that — it still expires, and it still cannot
/// be re-pointed at another request's archive by editing a path segment.
/// </para>
/// <para>
/// <b>The signing key is the document viewer's.</b> Two keys would be two things to configure and
/// two things to forget; what matters is that the <em>payloads</em> cannot collide, which the
/// request id and the distinct field order give.
/// </para>
/// </remarks>
public interface IPdpaArtifactLinks
{
    /// <summary>A link to <paramref name="storageUrl"/>, good until the returned instant.</summary>
    (string Url, DateTimeOffset ExpiresAt) Signed(Guid requestId, string storageUrl);

    /// <summary>The object key an export archive is written under.</summary>
    string KeyFor(Guid requestId);
}

/// <inheritdoc cref="IPdpaArtifactLinks"/>
internal sealed class PdpaArtifactLinks : IPdpaArtifactLinks
{
    private readonly AdminBffOptions.DocumentOptions _documents;
    private readonly AdminBffOptions.PdpaOptions _pdpa;
    private readonly IObjectStore _objects;
    private readonly TimeProvider _clock;
    private readonly byte[] _key;

    public PdpaArtifactLinks(
        IOptions<AdminBffOptions> options,
        IObjectStore objects,
        TimeProvider clock,
        ILogger<PdpaArtifactLinks> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _documents = options.Value.Documents;
        _pdpa = options.Value.Pdpa;
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _clock = clock ?? TimeProvider.System;

        if (string.IsNullOrWhiteSpace(_documents.SigningKey))
        {
            _key = RandomNumberGenerator.GetBytes(32);

            logger.LogWarning(
                "AdminBff:Documents:SigningKey is not configured, so a PDPA export link signed by this process "
                + "does not verify on another replica. Harmless once Storage:S3:* is configured — the bucket "
                + "signs its own reads then — and a broken download for a data subject otherwise.");
        }
        else
        {
            _key = Encoding.UTF8.GetBytes(_documents.SigningKey);
        }
    }

    /// <remarks>
    /// Under <c>pdpa/exports/</c> and keyed on the request id this service minted — never on
    /// anything a client sent, which is the kernel's rule for building an object key. The retention
    /// class prefix is the store's own doing; see <c>PdpaService</c> for why an export is ephemeral.
    /// </remarks>
    public string KeyFor(Guid requestId) => $"pdpa/exports/{requestId:N}.zip";

    public (string Url, DateTimeOffset ExpiresAt) Signed(Guid requestId, string storageUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageUrl);

        var expiresAt = _clock.GetUtcNow() + _pdpa.ArtifactUrlTtl;

        if (_objects.TryPresign(storageUrl, _pdpa.ArtifactUrlTtl, out var presigned))
        {
            return (presigned, expiresAt);
        }

        var expires = expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var target = Resolve(storageUrl);
        var separator = target.Contains('?', StringComparison.Ordinal) ? '&' : '?';

        return (
            $"{target}{separator}expires={expires}&signature={Sign(requestId, expires, storageUrl)}",
            expiresAt);
    }

    /// <summary>The stored pointer as something a browser can follow. Unset base URL passes it through.</summary>
    private string Resolve(string storageUrl)
    {
        if (string.IsNullOrWhiteSpace(_documents.PublicBaseUrl)
            || (Uri.TryCreate(storageUrl, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https"))
        {
            return storageUrl;
        }

        return $"{_documents.PublicBaseUrl.TrimEnd('/')}/{storageUrl.TrimStart('/')}";
    }

    /// <remarks>
    /// The literal <c>pdpa</c> is in the payload as a domain separator: it is what makes a signature
    /// minted here unusable against the document viewer's verifier and the reverse, even though both
    /// hash under the same key.
    /// </remarks>
    private string Sign(Guid requestId, string expires, string storageUrl) =>
        Convert.ToHexString(HMACSHA256.HashData(
            _key, Encoding.UTF8.GetBytes($"pdpa\n{requestId:D}\n{expires}\n{storageUrl}"))).ToLowerInvariant();
}
