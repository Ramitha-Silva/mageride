using System.Net;
using System.Net.Http.Json;
using MageRide.Fare.Configuration;
using MageRide.Shared.Errors;
using MageRide.Fare.Endpoints;
using MageRide.Shared.Http;
using Microsoft.Extensions.Options;

namespace MageRide.Fare.Settlement;

/// <summary>One row of <c>dispatch.cancellation_penalties</c>, as dispatch-svc reports it.</summary>
public sealed record CancellationPenalty(
    Guid PenaltyId,
    Guid PassengerId,
    Guid OriginalRideId,
    Guid AffectedDriverId,
    long AmountMinor,
    string Currency,
    string Basis,
    string Status);

/// <summary>What one settle call collected.</summary>
public sealed record PenaltySettlement(IReadOnlyList<CancellationPenalty> Items, long SettledMinor, string Currency)
{
    public static readonly PenaltySettlement Nothing = new([], 0, Domain.FareFormula.Currency);
}

/// <summary>
/// dispatch-svc's D-05 ledger of accrued cancellation debt (D5' §7.1).
/// </summary>
/// <remarks>
/// <b>The debt is not this service's to hold.</b> `dispatch.cancellation_penalties` is written when
/// a ride is cancelled after acceptance — dispatch-svc's plane — and read here only at the moment it
/// is collected. D3' names no route to reach it, so C035 added the two internal ones this client
/// calls; the C049 handoff records that they are now consumed.
/// </remarks>
internal interface IPenaltyClient
{
    /// <summary>
    /// Marks a passenger's outstanding penalties settled against a completed ride and returns what
    /// was collected.
    /// </summary>
    /// <remarks>
    /// <b>Settle first, then add what comes back to the fare.</b> That is C035 decision (9): the
    /// route is idempotent on <c>(penalty_id, applied_ride_id)</c>, so a retried settlement returns
    /// nothing and cannot charge twice — whereas reading the debt, pricing it, and settling
    /// afterwards would charge it again on every retry that failed after the price.
    /// </remarks>
    Task<PenaltySettlement> SettleAsync(Guid passengerId, Guid rideId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPenaltyClient"/>
internal sealed class PenaltyClient(
    IHttpClientFactory clients, IOptions<FareOptions> options, ILogger<PenaltyClient> logger) : IPenaltyClient
{
    public const string HttpClientName = "dispatch-penalties";

    private readonly FareOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<PenaltySettlement> SettleAsync(
        Guid passengerId, Guid rideId, CancellationToken cancellationToken)
    {
        if (!_options.PenaltySettlementEnabled || string.IsNullOrWhiteSpace(_options.DispatchBaseUrl))
        {
            return PenaltySettlement.Nothing;
        }

        var client = clients.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"v1/internal/passengers/{passengerId}/penalties/settle")
        {
            Content = JsonContent.Create(new { rideId = rideId.ToString() }, options: MageRideJson.Options),
        };

        request.Headers.TryAddWithoutValidation(InternalKeyFilter.ApiKeyHeader, _options.DispatchInternalApiKey);
        request.Headers.TryAddWithoutValidation(MageRideHeaders.IdempotencyKey, $"{rideId:D}");

        using var response = await client.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Degrade rather than refuse: the trip's own fare is correct and the debt stays
            // outstanding for the next completed trip to collect. Refusing here would leave a
            // driver unable to finish a ride because a *different* service was unwell.
            logger.LogError(
                "dispatch-svc answered {Status} settling passenger {PassengerId}'s cancellation penalties on "
                + "ride {RideId}. The fare is charged without them and the debt remains outstanding (D-05).",
                (int)response.StatusCode,
                passengerId,
                rideId);

            return PenaltySettlement.Nothing;
        }

        return await response.Content.ReadFromJsonAsync<PenaltySettlement>(MageRideJson.Options, cancellationToken)
               ?? PenaltySettlement.Nothing;
    }
}

/// <summary>The result of one ledger posting.</summary>
public sealed record LedgerPostingResult(Guid? EntryId, bool Replayed);

/// <summary>
/// wallet-svc's internal ledger seam (D-09). <b>This service writes no journal row itself.</b>
/// </summary>
/// <remarks>
/// The same seam and the same shape subscription-svc's <c>WalletLedgerClient</c> uses, for the same
/// reason: <c>billing.journal_postings</c> keeps exactly one writer, and it is wallet-svc (C046).
/// fare-svc decides that money is owed and asks; it never posts.
/// </remarks>
internal interface IWalletLedgerClient
{
    /// <summary>Whether the seam is configured at all.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Moves <paramref name="amountMinor"/> out of a driver's wallet.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when the posting could not be made — the caller decides what that
    /// means for its own transaction rather than having an exception decide for it.
    /// </returns>
    Task<LedgerPostingResult?> DebitAsync(
        Guid driverId, long amountMinor, string kind, string idempotencyKey,
        string description, string? reference, CancellationToken cancellationToken);

    /// <inheritdoc cref="DebitAsync"/>
    Task<LedgerPostingResult?> CreditAsync(
        Guid driverId, long amountMinor, string kind, string idempotencyKey,
        string description, string? reference, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IWalletLedgerClient"/>
internal sealed class WalletLedgerClient(
    IHttpClientFactory clients, IOptions<FareOptions> options, ILogger<WalletLedgerClient> logger)
    : IWalletLedgerClient
{
    public const string HttpClientName = "wallet-ledger";

    private readonly FareOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.WalletBaseUrl) && !string.IsNullOrWhiteSpace(_options.WalletInternalApiKey);

    public Task<LedgerPostingResult?> DebitAsync(
        Guid driverId, long amountMinor, string kind, string idempotencyKey,
        string description, string? reference, CancellationToken cancellationToken) =>
        PostAsync("debit", driverId, amountMinor, kind, idempotencyKey, description, reference, cancellationToken);

    public Task<LedgerPostingResult?> CreditAsync(
        Guid driverId, long amountMinor, string kind, string idempotencyKey,
        string description, string? reference, CancellationToken cancellationToken) =>
        PostAsync("credit", driverId, amountMinor, kind, idempotencyKey, description, reference, cancellationToken);

    private async Task<LedgerPostingResult?> PostAsync(
        string direction,
        Guid driverId,
        long amountMinor,
        string kind,
        string idempotencyKey,
        string description,
        string? reference,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        var client = clients.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"v1/internal/wallet/{driverId}/{direction}")
        {
            Content = JsonContent.Create(
                new { amountMinor, kind, idempotencyKey, description, reference }, options: MageRideJson.Options),
        };

        request.Headers.TryAddWithoutValidation(InternalKeyFilter.ApiKeyHeader, _options.WalletInternalApiKey);

        using var response = await client.SendAsync(request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<LedgerPostingResult>(
                MageRideJson.Options, cancellationToken) ?? new LedgerPostingResult(null, false);
        }

        // A 402 is a real answer, not an outage: the driver's wallet will not cover it. It is
        // carried back as "not posted" and the caller logs the consequence, because the D-05 leg is
        // a pass-through the driver has already collected in cash — a wallet too empty to forward it
        // is a reconciliation matter, not a reason to fail the passenger's ride.
        logger.LogError(
            "wallet-svc answered {Status} on a {Kind} {Direction} of {AmountMinor} for driver {DriverId} "
            + "(key {Key}). No ledger entry was posted.",
            (int)response.StatusCode,
            kind,
            direction,
            amountMinor,
            driverId,
            idempotencyKey);

        return response.StatusCode is HttpStatusCode.PaymentRequired
            ? null
            : throw new MageRideException(
                MageRideErrors.DependencyUnavailable,
                "The ledger could not be reached to settle a cancellation penalty.");
    }
}
