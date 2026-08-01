using System.Net;
using System.Net.Http.Json;
using MageRide.FleetBilling.Configuration;
using MageRide.Shared.Errors;
using MageRide.Shared.Http;
using Microsoft.Extensions.Options;

namespace MageRide.FleetBilling.Wallet;

/// <summary>What wallet-svc's internal seam answers (C046's <c>LedgerPostingResultResponse</c>).</summary>
/// <param name="Replayed">
/// <see langword="true"/> when the idempotency key had already been used — nothing was written and
/// the balance is the one the first attempt left. This is how a duplicate settlement reports itself,
/// and it is a <em>success</em>: the money has moved, once, and the caller's job is to record it.
/// </param>
public sealed record LedgerPosting(
    Guid EntryId, Guid AccountId, long AmountMinor, long BalanceAfterMinor, bool Replayed);

/// <summary>An organisation's ledger account, as wallet-svc resolved it.</summary>
public sealed record LedgerAccount(
    Guid AccountId, Guid OwnerId, string OwnerType, string Currency, long BalanceMinor);

/// <summary>The three calls this service makes against the ledger.</summary>
internal interface IFleetLedgerClient
{
    /// <summary>Whether the seam is configured at all. False ⇒ nothing can be settled or credited.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Resolves — creating if needed — the organisation's <c>owner_type='fleet'</c> account. Moves
    /// no money.
    /// </summary>
    /// <remarks>
    /// The only call here that is not a posting. <c>billing.accounts</c> has one writer (C046) and
    /// creates a fleet's row lazily on its first movement, which would leave a top-up session unable
    /// to record which wallet it credits until the organisation had already been invoiced once.
    /// </remarks>
    Task<LedgerAccount> EnsureAccountAsync(Guid fleetId, CancellationToken cancellationToken);

    /// <summary>
    /// Debits the fleet wallet through <c>POST /v1/internal/wallet/fleet/{fleetId}/debit</c>.
    /// </summary>
    /// <exception cref="MageRideException">
    /// <c>insufficient-wallet</c> (402) when the organisation cannot cover it — which is not an
    /// error but the ordinary reason an invoice stays open; <c>dependency-unavailable</c> (503) when
    /// the seam is unconfigured or wallet-svc is down.
    /// </exception>
    Task<LedgerPosting> DebitAsync(
        Guid fleetId,
        long amountMinor,
        string kind,
        string idempotencyKey,
        string? description,
        string? reference,
        CancellationToken cancellationToken);

    /// <summary>Credits the fleet wallet through <c>POST /v1/internal/wallet/fleet/{fleetId}/credit</c>.</summary>
    Task<LedgerPosting> CreditAsync(
        Guid fleetId,
        long amountMinor,
        string kind,
        string idempotencyKey,
        string? description,
        string? reference,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IFleetLedgerClient"/>
/// <remarks>
/// <para>
/// <b>This service writes no ledger row.</b> <c>billing.journal_postings</c> has exactly one writer
/// on this platform (D-09, C046), and every service that moves money without owning the ledger comes
/// through the same seam — subscription-svc's daily fee, fare-svc's tips and penalty settles,
/// admin-bff's fee reversal, and this component's invoice settlement and top-up credit. The fence
/// "postings use the same billing ledger with <c>owner_type='fleet'</c>; no parallel ledger" is
/// therefore held by an absence: there is no <c>INSERT INTO billing.journal_</c> anywhere in this
/// assembly, and no <c>UPDATE billing.accounts</c> either.
/// </para>
/// <para>
/// <b>The idempotency key is in the body, not in a header</b>, because the body's key is the
/// business fact: <c>fleet_invoice:{invoiceId}</c> and <c>fleet_topup:{topupId}</c> become
/// <c>billing.journal_entries.idempotency_key</c>, which is UNIQUE. That is what makes two replicas
/// settling one invoice at the same instant move the money once — a header-based guard over the same
/// money would be weaker and would need its own table.
/// </para>
/// <para>
/// <b>A 402 is an outcome, not a failure.</b> An organisation whose wallet cannot cover the month is
/// exactly what dunning exists for: the invoice stays open, the run counts it, and the next tick
/// tries again after a top-up. Retrying inside the call would move nothing and delay the answer,
/// which is why the resilience pipeline is not attached to a fresh attempt on 402.
/// </para>
/// </remarks>
internal sealed class FleetLedgerClient(
    IHttpClientFactory clients,
    IOptions<FleetBillingOptions> options,
    ILogger<FleetLedgerClient> logger) : IFleetLedgerClient
{
    /// <summary>Carries <c>FleetBilling:WalletInternalApiKey</c>. Replaced by the mesh identity in C042.</summary>
    public const string ApiKeyHeader = "X-MageRide-Internal-Key";

    /// <summary>The named client the D6' §8.3 resilience pipeline is attached to.</summary>
    public const string HttpClientName = "wallet-ledger";

    private readonly FleetBillingOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.WalletBaseUrl)
        && !string.IsNullOrWhiteSpace(_options.WalletInternalApiKey);

    public async Task<LedgerAccount> EnsureAccountAsync(Guid fleetId, CancellationToken cancellationToken)
    {
        RequireConfigured();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"v1/internal/wallet/fleet/{fleetId}/account");
        request.Headers.TryAddWithoutValidation(ApiKeyHeader, _options.WalletInternalApiKey);

        var client = clients.CreateClient(HttpClientName);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                          && !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "wallet-svc did not answer the account resolve for fleet {FleetId}.", fleetId);

            throw new MageRideException(
                MageRideErrors.DependencyUnavailable,
                "wallet-svc did not answer, so the fleet wallet could not be resolved.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new MageRideException(
                    MageRideErrors.DependencyUnavailable,
                    $"wallet-svc answered {(int)response.StatusCode} to the fleet account resolve.");
            }

            return await response.Content.ReadFromJsonAsync<LedgerAccount>(MageRideJson.Options, cancellationToken)
                   ?? throw new MageRideException(
                       MageRideErrors.DependencyUnavailable,
                       "wallet-svc answered the fleet account resolve with an empty body.");
        }
    }

    public Task<LedgerPosting> DebitAsync(
        Guid fleetId,
        long amountMinor,
        string kind,
        string idempotencyKey,
        string? description,
        string? reference,
        CancellationToken cancellationToken) =>
        PostAsync(fleetId, "debit", amountMinor, kind, idempotencyKey, description, reference, cancellationToken);

    public Task<LedgerPosting> CreditAsync(
        Guid fleetId,
        long amountMinor,
        string kind,
        string idempotencyKey,
        string? description,
        string? reference,
        CancellationToken cancellationToken) =>
        PostAsync(fleetId, "credit", amountMinor, kind, idempotencyKey, description, reference, cancellationToken);

    private async Task<LedgerPosting> PostAsync(
        Guid fleetId,
        string direction,
        long amountMinor,
        string kind,
        string idempotencyKey,
        string? description,
        string? reference,
        CancellationToken cancellationToken)
    {
        RequireConfigured();

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"v1/internal/wallet/fleet/{fleetId}/{direction}")
        {
            Content = JsonContent.Create(
                new { amountMinor, kind, idempotencyKey, description, reference },
                options: MageRideJson.Options),
        };

        request.Headers.TryAddWithoutValidation(ApiKeyHeader, _options.WalletInternalApiKey);

        // Resolved per call rather than captured: a singleton holding one typed HttpClient holds one
        // message handler for the process's lifetime, which is how a service stops noticing that
        // wallet-svc moved.
        var client = clients.CreateClient(HttpClientName);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                          && !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                exception, "wallet-svc did not answer the fleet {Direction} for fleet {FleetId}.", direction, fleetId);

            throw new MageRideException(
                MageRideErrors.DependencyUnavailable,
                $"wallet-svc did not answer, so the fleet {direction} did not happen.");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.PaymentRequired)
            {
                throw new MageRideException(
                    MageRideErrors.InsufficientWallet,
                    "The fleet wallet cannot cover this. Top up the wallet to settle the invoice.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                logger.LogError(
                    "wallet-svc refused the fleet {Direction} for fleet {FleetId} with {Status}: {Body}",
                    direction,
                    fleetId,
                    (int)response.StatusCode,
                    body);

                throw new MageRideException(
                    MageRideErrors.DependencyUnavailable,
                    $"wallet-svc answered {(int)response.StatusCode} to the fleet {direction}.");
            }

            return await response.Content.ReadFromJsonAsync<LedgerPosting>(
                       MageRideJson.Options, cancellationToken)
                   ?? throw new MageRideException(
                       MageRideErrors.DependencyUnavailable,
                       $"wallet-svc answered the fleet {direction} with an empty body.");
        }
    }

    private void RequireConfigured()
    {
        if (IsConfigured)
        {
            return;
        }

        throw new MageRideException(
            MageRideErrors.DependencyUnavailable,
            "wallet-svc is not configured (FleetBilling:WalletBaseUrl / FleetBilling:WalletInternalApiKey), "
            + "so no fleet invoice can be settled and no top-up can be credited. Refusing rather than "
            + "recording a payment: a 200 here would mark an invoice PAID against money that never moved.");
    }
}
