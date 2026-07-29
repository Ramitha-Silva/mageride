using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MageRide.Dispatch.Configuration;
using MageRide.Shared.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Dispatch.Dispatching;

/// <summary>
/// The three commands dispatch-svc issues against ride-svc's <c>/v1/internal/rides/**</c>.
/// </summary>
/// <remarks>
/// <para>
/// dispatch-svc never writes <c>rides.state</c> (R-01; ADD §11.12 "ride-svc is the sole writer").
/// ADD §11.11's diagram draws the <c>UPDATE</c> happening here; the C022 handoff records why
/// sole-writer wins and the moves became commands instead.
/// </para>
/// <para>
/// <b>Every key is deterministic.</b> D3' §0 makes <c>Idempotency-Key</c> mandatory on POST and
/// ride-svc replays from <c>rides.command_log</c> (R-14). A random key per attempt would make a
/// retry after a timeout look like a second, different command — exactly the double-offer the log
/// exists to prevent — so the key is derived from what the call is *about*: the offer id, or the
/// ride id for the one-per-ride matching move.
/// </para>
/// <para>
/// <b>D3' §0 puts this family on mTLS.</b> No mesh exists until C042, so the hop carries the
/// shared secret ride-svc checks (<c>X-MageRide-Internal-Key</c>). A wrong or missing key is
/// answered <c>404</c> by ride-svc, deliberately indistinguishable from an unmapped route.
/// </para>
/// </remarks>
public interface IRideServiceClient
{
    /// <summary><c>Requested → Matching</c>. dispatch has begun the candidate build.</summary>
    Task<RideCommandResult> MarkMatchingAsync(Guid rideId, long? version, CancellationToken cancellationToken);

    /// <summary><c>Matching → Offered</c>, and the write that makes <c>offer.created</c> real (R-13).</summary>
    Task<OfferPlacedResult> PlaceOfferAsync(
        Guid rideId, Guid offerId, Guid driverId, Guid vehicleId, int ttlSeconds, CancellationToken cancellationToken);

    /// <summary>
    /// <c>Offered → Matching</c> because the window closed unanswered (R-04) or because the
    /// driver's EMQX session is gone (R-15).
    /// </summary>
    /// <param name="driverUnreachable">
    /// R-15. The only way to revoke an offer <em>inside</em> its 15 s window, and only because the
    /// grace has already proved the driver cannot answer it. ride-svc validates the reason; a
    /// caller that sets it wrongly gets the ordinary deadline guard back.
    /// </param>
    Task<RideCommandResult> ExpireOfferAsync(
        Guid rideId, Guid offerId, bool driverUnreachable, CancellationToken cancellationToken);

    /// <summary>
    /// <c>Matching → ExpiredNoDriver</c>: the cascade ran out of candidates or out of time
    /// (US-6A.11, ADD §11.12's "No candidates after N rounds OR timeout").
    /// </summary>
    /// <remarks>
    /// The fourth command, and the only one of the four already in D3' — <c>system-cancel</c> is
    /// part of the contract's internal family and C032 mapped it, naming dispatch-svc as a caller
    /// ("dispatch-svc on an expired grace or an exhausted candidate cascade"). The reason is
    /// <c>no_driver_found</c>, which is the single matrix cell that produces
    /// <c>ExpiredNoDriver</c>, and it resolves from <c>Matching</c> alone: a ride that is
    /// <c>Offered</c> still has a live candidate and is answered 400, which is the correct answer
    /// and the reason the caller waits for the offer to settle first.
    /// </remarks>
    Task<RideCommandResult> SystemCancelAsync(Guid rideId, string reason, CancellationToken cancellationToken);
}

/// <param name="Succeeded"><see langword="true"/> only on a 2xx.</param>
/// <param name="Status">The HTTP status, so a caller can tell a race (409/410) from a fault.</param>
/// <param name="ErrorCode">The RFC 7807 <c>code</c> member, when the answer carried one.</param>
public record RideCommandResult(bool Succeeded, HttpStatusCode Status, string? ErrorCode, long? Version)
{
    public bool IsRace => Status is HttpStatusCode.Conflict or HttpStatusCode.Gone or HttpStatusCode.NotFound;
}

/// <param name="OfferExpiresAt">
/// ride-svc's deadline, stamped from <b>its</b> clock. Everything downstream — the
/// <c>dispatch.offers</c> mirror, the <c>rides.timers</c> fire time, the Redis <c>PEXPIRE</c> —
/// is aligned to this value, because it is <c>offer_expires_at &gt; now()</c> on ride-svc that
/// decides an accept.
/// </param>
public sealed record OfferPlacedResult(
    bool Succeeded, HttpStatusCode Status, string? ErrorCode, long? Version, DateTimeOffset? OfferExpiresAt)
    : RideCommandResult(Succeeded, Status, ErrorCode, Version);

/// <inheritdoc cref="IRideServiceClient"/>
public sealed class RideServiceClient(
    HttpClient http, IOptions<DispatchOptions> options, ILogger<RideServiceClient> logger) : IRideServiceClient
{
    /// <summary>Named client, so C042 can swap the handler for an mTLS one in one place.</summary>
    public const string HttpClientName = "ride-svc-internal";

    /// <summary>The interim shared secret. Deleted with <c>ride.yaml</c>'s <c>internalKey</c> scheme.</summary>
    public const string ApiKeyHeader = "X-MageRide-Internal-Key";

    private readonly DispatchOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public Task<RideCommandResult> MarkMatchingAsync(Guid rideId, long? version, CancellationToken cancellationToken) =>
        SendAsync(
            $"/v1/internal/rides/{rideId}/matching",
            new { version },
            IdempotencyKey("matching", rideId),
            cancellationToken);

    public async Task<OfferPlacedResult> PlaceOfferAsync(
        Guid rideId, Guid offerId, Guid driverId, Guid vehicleId, int ttlSeconds, CancellationToken cancellationToken)
    {
        // No `version` is echoed. dispatch has just come from a state it did not observe under a
        // lock, so pinning the version here would turn every harmless interleaving — a second
        // worker's matching call, a cancellation that lost — into a version-conflict retry loop.
        // ride-svc's own `state = 'Matching'` predicate is the guard that matters.
        var result = await SendAsync(
            $"/v1/internal/rides/{rideId}/offer",
            new
            {
                offerId = offerId.ToString(),
                driverId = driverId.ToString(),
                vehicleId = vehicleId.ToString(),
                ttlSeconds,
            },
            IdempotencyKey("offer", offerId),
            cancellationToken,
            readOfferExpiry: true);

        return (OfferPlacedResult)result;
    }

    /// <summary>The two values <c>ride.yaml</c>'s <c>offer/expire</c> body accepts (Δ C034).</summary>
    private const string DeadlineReason = "deadline";
    private const string DriverUnreachableReason = "driver_unreachable";

    public Task<RideCommandResult> ExpireOfferAsync(
        Guid rideId, Guid offerId, bool driverUnreachable, CancellationToken cancellationToken)
    {
        var reason = driverUnreachable ? DriverUnreachableReason : DeadlineReason;

        // The reason is part of the key. The two are different commands against the same offer —
        // one waited for the deadline and one did not — and a shared key would replay the first
        // answer for the second: a backstop that lost a race to the grace would be told 409 for
        // ever instead of finding the offer already settled.
        return SendAsync(
            $"/v1/internal/rides/{rideId}/offer/expire",
            new { offerId = offerId.ToString(), reason },
            IdempotencyKey($"expire-{reason}", offerId),
            cancellationToken);
    }

    public Task<RideCommandResult> SystemCancelAsync(Guid rideId, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        // Keyed by the ride and the reason, not by the ride alone: a ride can in principle be
        // system-cancelled for two different reasons over its life, and a key that collapsed them
        // would replay the first answer for the second command.
        return SendAsync(
            $"/v1/internal/rides/{rideId}/system-cancel",
            new { reason },
            IdempotencyKey($"syscancel-{reason}", rideId),
            cancellationToken);
    }

    /// <summary>
    /// A stable key per (operation, subject). Long enough for the kernel's 16-character minimum and
    /// well under its 128-character ceiling.
    /// </summary>
    internal static string IdempotencyKey(string operation, Guid subject) =>
        string.Create(CultureInfo.InvariantCulture, $"dispatch-{operation}-{subject}");

    private async Task<RideCommandResult> SendAsync(
        string path,
        object body,
        string idempotencyKey,
        CancellationToken cancellationToken,
        bool readOfferExpiry = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: MageRideJson.Options),
        };

        request.Headers.Add("Idempotency-Key", idempotencyKey);

        if (!string.IsNullOrWhiteSpace(_options.RideServiceInternalKey))
        {
            request.Headers.Add(ApiKeyHeader, _options.RideServiceInternalKey);
        }

        using var response = await http.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var code = ReadErrorCode(payload);

            // A 409/410 is the normal shape of a race — the driver answered while the backstop was
            // in flight, or another worker got there first — so it is information, not a failure.
            logger.LogInformation(
                "ride-svc answered {Status} ({ErrorCode}) to POST {Path}", (int)response.StatusCode, code, path);

            return readOfferExpiry
                ? new OfferPlacedResult(false, response.StatusCode, code, null, null)
                : new RideCommandResult(false, response.StatusCode, code, null);
        }

        var (version, expiresAt) = ReadSuccess(payload);

        return readOfferExpiry
            ? new OfferPlacedResult(true, response.StatusCode, null, version, expiresAt)
            : new RideCommandResult(true, response.StatusCode, null, version);
    }

    private static (long? Version, DateTimeOffset? OfferExpiresAt) ReadSuccess(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            long? version = root.TryGetProperty("version", out var v) && v.TryGetInt64(out var parsed) ? parsed : null;

            DateTimeOffset? expiresAt =
                root.TryGetProperty("offerExpiresAt", out var e) && e.ValueKind is JsonValueKind.String &&
                e.TryGetDateTimeOffset(out var deadline)
                    ? deadline
                    : null;

            return (version, expiresAt);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// The kebab code out of an RFC 7807 body. D3' §0 carries it in the <c>type</c> URI
    /// (<c>https://mageride.lk/errors/{code}</c>) rather than as its own member, so this is the
    /// last path segment.
    /// </summary>
    private static string? ReadErrorCode(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);

            if (!document.RootElement.TryGetProperty("type", out var type) || type.GetString() is not { } uri)
            {
                return null;
            }

            var slash = uri.LastIndexOf('/');
            return slash >= 0 && slash < uri.Length - 1 ? uri[(slash + 1)..] : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
