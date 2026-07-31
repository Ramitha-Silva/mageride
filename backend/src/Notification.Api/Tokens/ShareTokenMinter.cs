using System.Security.Cryptography;
using Dapper;
using MageRide.Notification.Configuration;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.Notification.Tokens;

/// <summary>The four <c>safety.trip_share_tokens.scope</c> values (migration 0901).</summary>
public static class ShareTokenScopes
{
    /// <summary>D-34's trip share. Issued by safety-svc (C052), not here.</summary>
    public const string TripView = "trip_view";

    /// <summary>P-09 / AL-21: the unregistered package recipient's tracking link.</summary>
    public const string PackageRecipient = "package_recipient";

    /// <summary>AL-44 / US-8.22: the proxy rider's link, minted when a driver accepts.</summary>
    public const string ProxyRider = "proxy_rider";

    /// <summary>AL-45: the unregistered rider confirming a pickup point. TTL 300 s.</summary>
    public const string PickupConfirm = "pickup_confirm";
}

/// <summary>
/// Mints an AL-44 share token and turns it into the link an SMS carries.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the component's first fence, and it is held by the type system.</b> D3' Δ 2026-07-05
/// and D6' I-29.2 both say the token is "mint-and-SMS, never returned to a client", so
/// <see cref="MintAsync"/> returns a <see cref="MintedLink"/> whose only public member is the URL —
/// which goes into a template's <c>{{link}}</c> and into no response body in this assembly. There
/// is no endpoint that mints one and no contract shape that could carry one out.
/// </para>
/// <para>
/// <b>The token is the whole credential for an unauthenticated page</b>, so it is 32 bytes from
/// <see cref="RandomNumberGenerator"/>, base64url, and it is the primary key of its own row — the
/// design 0901 chose so that "a surrogate id would only add a second thing to keep secret".
/// </para>
/// <para>
/// <b>Revocation and access metering are safety-svc's</b> (C052's deliverable, AL-44). This
/// component writes the row and its expiry; burning a <c>pickup_confirm</c> token on use is
/// public-bff's, per BR-29.1.
/// </para>
/// </remarks>
public interface IShareTokenMinter
{
    /// <summary>
    /// Mints a token scoped to a trip (<c>package_recipient</c>, <c>proxy_rider</c>).
    /// </summary>
    Task<MintedLink> MintForTripAsync(
        string scope, Guid tripId, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    /// <summary>
    /// Mints AL-45's <c>pickup_confirm</c> token, bound to a location request rather than a trip —
    /// the row exists before the ride does, which is why <c>trip_id</c> is nullable in 0901.
    /// </summary>
    /// <remarks>
    /// The referent is <c>rides.location_requests.id</c>, the surrogate — <b>not</b> the public
    /// <c>request_id</c> handle. 0901's foreign key points at the primary key, and the two are
    /// deliberately distinct (0606: "so the public handle can be rotated without touching FKs").
    /// </remarks>
    Task<MintedLink> MintForLocationRequestAsync(
        Guid locationRequestId, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    /// <summary>True when a link can be built at all (<c>Notification:WebTrackBaseUrl</c>).</summary>
    bool IsConfigured { get; }
}

/// <summary>
/// A minted token, exposed only as the link that carries it.
/// </summary>
/// <remarks>
/// The token value is deliberately not a property. A record with a <c>Token</c> member would be one
/// refactor away from being serialised into a response, and AL-44/AL-45's fence is that no client
/// API ever returns one.
/// </remarks>
public sealed class MintedLink
{
    internal MintedLink(string url, DateTimeOffset expiresAt)
    {
        Url = url;
        ExpiresAt = expiresAt;
    }

    /// <summary>The full <c>passenger.mageride.lk/track?token=…</c> URL for an SMS body.</summary>
    public string Url { get; }

    public DateTimeOffset ExpiresAt { get; }
}

/// <inheritdoc cref="IShareTokenMinter"/>
internal sealed class ShareTokenMinter(
    INpgsqlConnectionFactory connections,
    IOptions<NotificationOptions> options) : IShareTokenMinter
{
    private readonly INpgsqlConnectionFactory _connections =
        connections ?? throw new ArgumentNullException(nameof(connections));

    private readonly NotificationOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.WebTrackBaseUrl);

    public async Task<MintedLink> MintForTripAsync(
        string scope, Guid tripId, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        if (scope is ShareTokenScopes.PickupConfirm)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scope), scope, "A pickup_confirm token is bound to a location request, not a trip (0901).");
        }

        return await InsertAsync(scope, tripId, locationRequestId: null, expiresAt, cancellationToken);
    }

    public Task<MintedLink> MintForLocationRequestAsync(
        Guid locationRequestId, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
        InsertAsync(
            ShareTokenScopes.PickupConfirm, tripId: null, locationRequestId, expiresAt, cancellationToken);

    private async Task<MintedLink> InsertAsync(
        string scope,
        Guid? tripId,
        Guid? locationRequestId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Notification:WebTrackBaseUrl is not configured, so a share token cannot be turned into a link. "
                + "The SMS is not sent — a tracking message with no URL is worse than none (AL-44).");
        }

        var token = NewToken(_options.ShareTokenBytes);

        await using var connection = await _connections.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO safety.trip_share_tokens (token, trip_id, scope, location_request_id, expires_at)
                VALUES (@Token, @TripId, @Scope, @LocationRequestId, @ExpiresAt);
                """,
                new
                {
                    Token = token,
                    TripId = tripId,
                    Scope = scope,
                    LocationRequestId = locationRequestId,
                    ExpiresAt = expiresAt,
                },
                cancellationToken: cancellationToken));

        return new MintedLink(_options.WebTrackBaseUrl + Uri.EscapeDataString(token), expiresAt);
    }

    /// <summary>
    /// Base64url over <paramref name="bytes"/> cryptographic bytes. No padding, so the value is
    /// URL-safe without escaping and an SMS does not spend characters on <c>%3D</c>.
    /// </summary>
    internal static string NewToken(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
