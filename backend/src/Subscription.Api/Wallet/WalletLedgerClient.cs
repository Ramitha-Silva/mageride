using System.Net;
using System.Net.Http.Json;
using MageRide.Shared.Errors;
using MageRide.Shared.Http;
using MageRide.Subscriptions.Configuration;
using Microsoft.Extensions.Options;

namespace MageRide.Subscriptions.Wallet;

/// <summary>What wallet-svc's internal seam answers (C046's <c>LedgerPostingResultResponse</c>).</summary>
/// <param name="Replayed">
/// <see langword="true"/> when the idempotency key had already been used — nothing was written and the
/// balance is the one the first attempt left. This is how a duplicate charge reports itself.
/// </param>
public sealed record LedgerPosting(
    Guid EntryId, Guid AccountId, long AmountMinor, long BalanceAfterMinor, bool Replayed);

/// <summary>The single call this service makes to move a driver's money.</summary>
internal interface IWalletLedgerClient
{
    /// <summary>Whether the seam is configured at all. False ⇒ nothing can be charged.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Debits a driver's wallet through <c>POST /v1/internal/wallet/{driverId}/debit</c>.
    /// </summary>
    /// <exception cref="MageRideException">
    /// <c>insufficient-wallet</c> (402) when the driver cannot cover it — D-08's own answer, arriving
    /// late; <c>dependency-unavailable</c> (503) when the seam is unconfigured or wallet-svc is down.
    /// </exception>
    Task<LedgerPosting> DebitAsync(
        Guid driverId,
        long amountMinor,
        string kind,
        string idempotencyKey,
        string? description,
        string? reference,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IWalletLedgerClient"/>
/// <remarks>
/// <para>
/// <b>This service writes no ledger row.</b> <c>billing.journal_postings</c> has exactly one writer on
/// this platform (D-09, C046), and every service that moves a driver's money without owning the ledger
/// comes through the same seam — subscription-svc's daily fee, fare-svc's tips and penalty settles,
/// admin-bff's fee reversal. Four callers, one balanced-entry implementation, one non-negativity rule,
/// one D-08 cache write-through, one outbox.
/// </para>
/// <para>
/// <b>The idempotency key is in the body, not in a header</b>, because the body's key is the business
/// fact: <c>daily_fee:{driverId}:{vehicleId}:{feeDate}</c> becomes
/// <c>billing.journal_entries.idempotency_key</c>, which is UNIQUE. That is what makes two replicas
/// charging the same driver at the same instant move the money once — a header-based guard over the
/// same money would be weaker and would need its own table.
/// </para>
/// <para>
/// <b>A 402 is not a failure to retry.</b> It is the D-08 gate's answer arriving on the accept path:
/// the driver cannot cover the day's fee, the accept is refused, and US-9.1's "request missed:
/// insufficient balance" is what the driver sees. Retrying it would charge nothing and delay the
/// refusal.
/// </para>
/// </remarks>
internal sealed class WalletLedgerClient(
    IHttpClientFactory clients,
    IOptions<SubscriptionOptions> options,
    ILogger<WalletLedgerClient> logger) : IWalletLedgerClient
{
    /// <summary>Carries <c>Subscription:WalletInternalApiKey</c>. Replaced by the mesh identity in C042.</summary>
    public const string ApiKeyHeader = "X-MageRide-Internal-Key";

    /// <summary>The named client the D6' §8.3 resilience pipeline is attached to.</summary>
    public const string HttpClientName = "wallet-ledger";

    private readonly SubscriptionOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.WalletBaseUrl)
        && !string.IsNullOrWhiteSpace(_options.WalletInternalApiKey);

    public async Task<LedgerPosting> DebitAsync(
        Guid driverId,
        long amountMinor,
        string kind,
        string idempotencyKey,
        string? description,
        string? reference,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new MageRideException(
                MageRideErrors.DependencyUnavailable,
                "wallet-svc is not configured (Subscription:WalletBaseUrl / Subscription:WalletInternalApiKey), "
                + "so no fee can be charged. Refusing rather than allowing the trip: a 200 here would cost "
                + "the platform its only revenue and look healthy while doing it.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"v1/internal/wallet/{driverId}/debit")
        {
            Content = JsonContent.Create(
                new { amountMinor, kind, idempotencyKey, description, reference },
                options: MageRideJson.Options),
        };

        request.Headers.TryAddWithoutValidation(ApiKeyHeader, _options.WalletInternalApiKey);

        // Resolved per call rather than captured: a singleton holding one typed HttpClient holds one
        // message handler for the process's lifetime, which is how a service stops noticing that
        // wallet-svc moved. The factory rotates handlers; the same reason query-svc's geocoder takes
        // one.
        var client = clients.CreateClient(HttpClientName);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                          && !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "wallet-svc did not answer the daily-fee debit for driver {DriverId}.", driverId);

            throw new MageRideException(
                MageRideErrors.DependencyUnavailable,
                "wallet-svc did not answer, so the daily fee could not be charged.");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.PaymentRequired)
            {
                // The wallet said no. Carried through with the code the driver's app already branches on
                // (US-9.1) rather than reshaped into a 503, which would look like an outage the driver
                // could wait out instead of a balance they have to top up.
                throw new MageRideException(
                    MageRideErrors.InsufficientWallet,
                    "The driver's wallet cannot cover today's platform fee. Top up to accept another trip.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                logger.LogError(
                    "wallet-svc refused the daily-fee debit for driver {DriverId} with {Status}: {Body}",
                    driverId,
                    (int)response.StatusCode,
                    body);

                throw new MageRideException(
                    MageRideErrors.DependencyUnavailable,
                    $"wallet-svc answered {(int)response.StatusCode} to the daily-fee debit.");
            }

            return await response.Content.ReadFromJsonAsync<LedgerPosting>(
                       MageRideJson.Options, cancellationToken)
                   ?? throw new MageRideException(
                       MageRideErrors.DependencyUnavailable,
                       "wallet-svc answered the daily-fee debit with an empty body.");
        }
    }
}
