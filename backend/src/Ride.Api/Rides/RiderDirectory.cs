using System.Text.Json;
using MageRide.Ride.Configuration;
using MageRide.Shared.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Ride.Rides;

/// <summary>Whether a number belongs to a MageRide account, and whose (P-03).</summary>
/// <param name="UserId">
/// The account, when there is one. <see langword="null"/> for an unregistered number — which is not
/// an error: AL-45 gives that case its own path.
/// </param>
public sealed record RiderLookup(bool Registered, Guid? UserId)
{
    public static readonly RiderLookup Unregistered = new(false, null);
}

/// <summary>
/// The proxy-booking registration check: iam-svc's <c>GET /v1/users/lookup</c> (ADD §11.15, P-03).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a call and not a join.</b> ride-svc already reads <c>iam.users</c> for
/// <c>counterpartyPhone</c> (<c>CounterpartyRepository</c>), so the row is reachable — but this
/// question is different in kind. It is a <b>registration oracle</b>: anyone who can ask it can
/// learn whether a stranger's number is on the platform, one number at a time. iam-svc answers it
/// behind <c>iam.phone_lookups</c>, which records who asked about which digest (C027), and that
/// audit is the control. A join would answer the same question with no record that it was ever
/// asked.
/// </para>
/// <para>
/// <b>An outage is a 503, not an assumption.</b> Guessing "unregistered" would silently downgrade
/// every rider to AL-45's SMS path — a real SMS to a real person, sent because a service was
/// restarting. Guessing "registered" would send an FCM message nobody receives and leave the booker
/// watching a request that can only expire. So the booker is told the platform could not answer and
/// the request is not created.
/// </para>
/// </remarks>
public interface IRiderDirectory
{
    Task<RiderLookup> LookupAsync(string phoneE164, CancellationToken cancellationToken);
}

/// <summary>
/// What stands in for the lookup when <c>Ride:IamBaseUrl</c> is not configured.
/// </summary>
/// <remarks>
/// It refuses rather than guesses, for the reason <see cref="IRiderDirectory"/> gives: the two
/// available guesses are "send a stranger an SMS" and "send an FCM message into the void". Only the
/// proxy and location-request routes touch it, so a deployment that never uses proxy booking works
/// exactly as before and one that does hears about the missing setting on the first attempt.
/// </remarks>
public sealed class UnconfiguredRiderDirectory : IRiderDirectory
{
    public Task<RiderLookup> LookupAsync(string phoneE164, CancellationToken cancellationToken) =>
        throw new MageRideException(
            MageRideErrors.DependencyUnavailable,
            "Ride:IamBaseUrl is not configured, so a rider's number cannot be checked against iam-svc (P-03). " +
            "Proxy booking and the location-request round-trip are unavailable on this deployment.");
}

/// <inheritdoc cref="IRiderDirectory"/>
public sealed class IamRiderDirectory(
    HttpClient http, IOptions<RideOptions> options, ILogger<IamRiderDirectory> logger) : IRiderDirectory
{
    /// <summary>Named client so C042 can swap the handler for an mTLS one in one place.</summary>
    public const string HttpClientName = "iam-svc-lookup";

    /// <summary>iam-svc's interim guard on the route (<c>Auth:InternalApiKey</c>, C027).</summary>
    public const string ApiKeyHeader = "X-MageRide-Internal-Key";

    private readonly RideOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<RiderLookup> LookupAsync(string phoneE164, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phoneE164);

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/v1/users/lookup?phone={Uri.EscapeDataString(phoneE164)}");

        if (!string.IsNullOrWhiteSpace(_options.IamInternalApiKey))
        {
            request.Headers.Add(ApiKeyHeader, _options.IamInternalApiKey);
        }

        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw Unavailable(exception, logger);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // 404 included. iam-svc answers 200 `{registered:false}` for a number it does not
                // know, so a 404 means the *route* is missing — the key is unset at that end, or
                // this is not iam-svc. Either way nobody has answered the question.
                logger.LogError(
                    "iam-svc answered {Status} to GET /v1/users/lookup; the proxy rider cannot be resolved",
                    (int)response.StatusCode);

                throw Unavailable(null, logger);
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);

            return Parse(payload)
                   ?? throw new MageRideException(
                       MageRideErrors.DependencyUnavailable,
                       "iam-svc's lookup answer could not be read; the rider's registration is unknown.");
        }
    }

    private static RiderLookup? Parse(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (!root.TryGetProperty("registered", out var registered) ||
                registered.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            if (!registered.GetBoolean())
            {
                return RiderLookup.Unregistered;
            }

            // `userId` is optional in the contract's schema and meaningless without it: a
            // "registered" answer naming nobody cannot produce an FCM target, so it is treated as
            // unreadable rather than as a rider.
            return root.TryGetProperty("userId", out var userId) &&
                   userId.ValueKind is JsonValueKind.String &&
                   Guid.TryParse(userId.GetString(), out var parsed)
                ? new RiderLookup(true, parsed)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MageRideException Unavailable(Exception? cause, ILogger logger)
    {
        if (cause is not null)
        {
            logger.LogError(cause, "iam-svc could not be reached for the proxy rider lookup (P-03)");
        }

        return new MageRideException(
            MageRideErrors.DependencyUnavailable,
            "The rider's number could not be checked against iam-svc, so the request was not issued. " +
            "Try again in a moment.");
    }
}
