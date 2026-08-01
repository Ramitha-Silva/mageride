using System.Net.Http.Json;
using MageRide.Payout.Configuration;
using MageRide.Payout.Domain;
using MageRide.Shared.Http;
using Microsoft.Extensions.Options;

namespace MageRide.Payout.Wallet;

/// <summary>What wallet-svc answered: the entry, and whether the key had already been used.</summary>
public sealed record LedgerPostingResult(Guid? EntryId, bool Replayed);

/// <summary>
/// wallet-svc's ledger seam (D-09). <b>This service writes no journal row itself.</b>
/// </summary>
/// <remarks>
/// The same arrangement fare-svc and fleet-billing-svc use, for the same reason:
/// <c>billing.journal_postings</c> keeps exactly one writer, and it is wallet-svc (C046). This
/// service decides that a payout is owed and asks; it never posts.
/// </remarks>
internal interface IPayoutLedgerClient
{
    bool IsConfigured { get; }

    /// <summary>Takes the swept amount out of the driver's wallet (AL-58).</summary>
    /// <remarks>
    /// Keyed <c>driver_payout:{payoutId}</c> on the far side, and the payout id is derived from
    /// <c>(batch, driver)</c> — see <see cref="PayoutIds"/>. That is what makes a re-run replay the
    /// debit instead of making a second one.
    /// </remarks>
    Task<LedgerPostingResult?> DebitAsync(
        Guid payoutId, Guid driverId, long amountMinor, CancellationToken cancellationToken);

    /// <summary>Puts a refused payout back on the driver's wallet, exactly once.</summary>
    /// <remarks>
    /// Keyed <c>driver_payout_reversal:{payoutId}</c> — a <em>second</em> key, not a second kind.
    /// Sharing the debit's key would make this a replay of the debit and restore nothing, so a
    /// driver whose bank transfer bounced would silently lose the week.
    /// </remarks>
    Task<LedgerPostingResult?> ReverseAsync(
        Guid payoutId, Guid driverId, long amountMinor, string? failureReason, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPayoutLedgerClient"/>
internal sealed class PayoutLedgerClient(
    IHttpClientFactory clients, IOptions<PayoutOptions> options, ILogger<PayoutLedgerClient> logger)
    : IPayoutLedgerClient
{
    public const string HttpClientName = "wallet-ledger";

    /// <summary>The header every <c>/v1/internal/**</c> plane on the platform guards itself with (C008).</summary>
    public const string ApiKeyHeader = "X-MageRide-Internal-Key";

    private readonly PayoutOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.WalletBaseUrl) && !string.IsNullOrWhiteSpace(_options.WalletInternalApiKey);

    public Task<LedgerPostingResult?> DebitAsync(
        Guid payoutId, Guid driverId, long amountMinor, CancellationToken cancellationToken) =>
        PostAsync(
            "v1/internal/wallet/driver-payout",
            new { payoutId, driverId, amountMinor },
            payoutId,
            cancellationToken);

    public Task<LedgerPostingResult?> ReverseAsync(
        Guid payoutId, Guid driverId, long amountMinor, string? failureReason, CancellationToken cancellationToken) =>
        PostAsync(
            $"v1/internal/wallet/driver-payout/{payoutId:D}/reverse",
            new { driverId, amountMinor, failureReason },
            payoutId,
            cancellationToken);

    private async Task<LedgerPostingResult?> PostAsync(
        string path, object body, Guid payoutId, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        var client = clients.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: MageRideJson.Options),
        };

        request.Headers.TryAddWithoutValidation(ApiKeyHeader, _options.WalletInternalApiKey);

        HttpResponseMessage response;

        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                                       && !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "wallet-svc could not be reached for payout {PayoutId}.", payoutId);
            return null;
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<LedgerPostingResult>(
                    MageRideJson.Options, cancellationToken) ?? new LedgerPostingResult(null, false);
            }

            // Deliberately not thrown. A driver whose balance moved between selection and debit
            // (they paid a daily fee in the same second) is not an error — they are simply not swept
            // this week, and the next run picks up whatever is there. Every other failure is loud
            // and leaves the driver's money exactly where it was.
            logger.LogError(
                "wallet-svc answered {Status} for payout {PayoutId}; the driver was not swept and their "
                + "balance is untouched.",
                (int)response.StatusCode,
                payoutId);

            return null;
        }
    }
}

/// <summary>
/// The one outbound port to a bank, and no provider is chosen.
/// </summary>
/// <remarks>
/// <para>
/// ADD §1.18 makes origination via LankaPay/CEFTS a sponsor-bank and CBSL question, which is a
/// go-live gate rather than an engineering task. So this is an interface with one HTTP
/// implementation and a documented "unconfigured" behaviour, and nothing in the run depends on
/// which provider eventually sits behind it.
/// </para>
/// <para>
/// <b>Unconfigured, the run still debits and still records.</b> Instructions rest at
/// <c>PENDING</c>, which is exactly what an operator needs to see: the liability is visible before
/// a rail exists. The alternative — refusing to sweep until a bank is wired — would hide the debt
/// in a growing wallet balance instead.
/// </para>
/// </remarks>
internal interface IBankOrigination
{
    bool IsConfigured { get; }

    /// <summary>Hands one instruction to the bank. Null when it could not be submitted.</summary>
    Task<string?> SubmitAsync(
        PayoutInstruction instruction, string accountNo, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IBankOrigination"/>
internal sealed class BankOrigination(
    IHttpClientFactory clients, IOptions<PayoutOptions> options, ILogger<BankOrigination> logger)
    : IBankOrigination
{
    public const string HttpClientName = "bank-origination";

    private readonly PayoutOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.BankBaseUrl);

    public async Task<string?> SubmitAsync(
        PayoutInstruction instruction, string accountNo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instruction);

        if (!IsConfigured)
        {
            return null;
        }

        var client = clients.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(HttpMethod.Post, "transfers")
        {
            Content = JsonContent.Create(
                new
                {
                    reference = instruction.Id,
                    amountMinor = instruction.AmountMinor,
                    currency = "LKR",
                    accountNo,
                },
                options: MageRideJson.Options),
        };

        if (!string.IsNullOrWhiteSpace(_options.BankApiKey))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_options.BankApiKey}");
        }

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "The bank refused payout {PayoutId} at submission with {Status}; it stays PENDING and the "
                    + "next run re-submits it. The driver's money has already left their wallet and is held "
                    + "against this instruction.",
                    instruction.Id,
                    (int)response.StatusCode);

                return null;
            }

            var accepted = await response.Content.ReadFromJsonAsync<BankAccepted>(
                MageRideJson.Options, cancellationToken);

            return accepted?.Reference;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                                       && !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "The bank could not be reached for payout {PayoutId}.", instruction.Id);
            return null;
        }
    }

    private sealed record BankAccepted(string? Reference);
}
