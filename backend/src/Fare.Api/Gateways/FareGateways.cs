using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Dapper;
using MageRide.Fare.Configuration;
using MageRide.Fare.Domain;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.Fare.Gateways;

/// <summary>What a gateway hands back for the app to open.</summary>
/// <param name="PaymentLink">AL-15's "Pay" deep link into the bank app; the QR is the fallback.</param>
public sealed record GatewaySession(
    string? RedirectUrl, string? SessionToken, string? PaymentLink, string? QrPayload)
{
    public static readonly GatewaySession None = new(null, null, null, null);
}

/// <summary>Opens a payment session for one ride fare.</summary>
/// <remarks>
/// <b>Deliberately not wallet-svc's <c>IPaymentGateway</c>, and the difference is D-11.</b> A wallet
/// top-up is money moving to the platform; a ride fare is money moving to a *driver*, which needs
/// that driver's OnePay merchant binding (<c>registry.driver_payouts</c>) or there is nowhere for it
/// to land — <c>402 merchant-not-onboarded</c>. The two also disagree on the fallback: a failed
/// top-up is a 503 and the driver picks the other rail, while a failed ride payment falls back to
/// cash (D6' §7.1). Promoting a common client into the kernel is worth doing when a third caller
/// appears; raised in the C050 handoff.
/// </remarks>
internal interface IFareGateway
{
    /// <summary>The <c>fares.ride_payments.method</c> this gateway serves.</summary>
    string Method { get; }

    bool IsConfigured { get; }

    /// <summary>Opens a session for one ride payment.</summary>
    Task<GatewaySession> StartAsync(
        Guid paymentId, Guid rideId, long amountMinor, string? merchantId, CancellationToken cancellationToken);
}

/// <summary>OnePay's create-session call (D6' §7.1), for a ride fare.</summary>
internal sealed class OnepayFareGateway(
    IHttpClientFactory clients, IOptions<FareOptions> options, ILogger<OnepayFareGateway> logger) : IFareGateway
{
    public const string HttpClientName = "onepay-fare";

    private readonly FareOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public string Method => RidePaymentMethods.Onepay;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.OnepayApiKey) && !string.IsNullOrWhiteSpace(_options.OnepayBaseUrl);

    public async Task<GatewaySession> StartAsync(
        Guid paymentId, Guid rideId, long amountMinor, string? merchantId, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new MageRideException(
                MageRideErrors.DependencyUnavailable,
                "OnePay is not configured on this deployment (Fare:OnepayApiKey / Fare:OnepayBaseUrl).");
        }

        var client = clients.CreateClient(HttpClientName);

        try
        {
            using var response = await client.PostAsJsonAsync(
                "sessions",
                new
                {
                    orderId = paymentId.ToString(),
                    // Minor units, which is how the whole platform transmits money (CLAUDE.md). A
                    // gateway that wants rupees is a mapping to make once, here, rather than a
                    // floating-point value travelling through the service.
                    amountMinor,
                    currency = FareFormula.Currency,
                    // D-11: the driver's own merchant sub-account. Without it the money has nowhere
                    // to land, which is why the caller refuses before reaching this method.
                    merchantId,
                    reference = rideId.ToString(),
                },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "OnePay refused a session for payment {PaymentId}: {Status}.",
                    paymentId, (int)response.StatusCode);

                throw new MageRideException(
                    MageRideErrors.GatewayError, $"OnePay returned {(int)response.StatusCode}.");
            }

            var session = await response.Content.ReadFromJsonAsync<OnepaySessionResponse>(cancellationToken);

            if (session is null
                || (string.IsNullOrWhiteSpace(session.RedirectUrl) && string.IsNullOrWhiteSpace(session.SessionToken)))
            {
                throw new MageRideException(
                    MageRideErrors.GatewayError,
                    "OnePay accepted the session but returned neither a redirect URL nor a session token.");
            }

            return new GatewaySession(session.RedirectUrl, session.SessionToken, null, null);
        }
        catch (HttpRequestException exception)
        {
            logger.LogError(exception, "OnePay was unreachable for payment {PaymentId}.", paymentId);

            throw new MageRideException(MageRideErrors.DependencyUnavailable, "OnePay is unreachable.", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MageRideException(
                MageRideErrors.UpstreamTimeout, "OnePay did not answer inside the timeout budget.", exception);
        }
    }

    private sealed record OnepaySessionResponse(
        [property: JsonPropertyName("redirectUrl")] string? RedirectUrl,
        [property: JsonPropertyName("sessionToken")] string? SessionToken);
}

/// <summary>
/// LankaQR / Commercial Bank IPG (D-12, D6' §7.2, AL-15).
/// </summary>
/// <remarks>
/// <para>
/// <b>No outbound call, and that is the integration.</b> D6' §7.2's LankaQR leg is a <em>deep link
/// into the passenger's own bank app</em> plus a confirm webhook — there is no session to create,
/// because the passenger's bank initiates the transfer and the acquirer tells us afterwards. So this
/// "gateway" composes a link and a QR payload from configuration and waits.
/// </para>
/// <para>
/// <b>The link is primary and the QR is the fallback</b> (AL-15, US-8.10a): the app shows a "Pay"
/// button, and renders the scannable code only when no compatible bank app is installed. That order
/// is why <see cref="GatewaySession.PaymentLink"/> is filled first and the payload second.
/// </para>
/// </remarks>
internal sealed class LankaQrFareGateway(IOptions<FareOptions> options) : IFareGateway
{
    private readonly FareOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public string Method => RidePaymentMethods.LankaQr;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.LankaQrMerchantId);

    public Task<GatewaySession> StartAsync(
        Guid paymentId, Guid rideId, long amountMinor, string? merchantId, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new MageRideException(
                MageRideErrors.DependencyUnavailable,
                "LankaQR is not configured on this deployment (Fare:LankaQrMerchantId).");
        }

        var amount = (amountMinor / 100m).ToString("0.00", CultureInfo.InvariantCulture);
        var reference = paymentId.ToString("N");

        var link = string.IsNullOrWhiteSpace(_options.LankaQrDeepLinkTemplate)
            ? null
            : _options.LankaQrDeepLinkTemplate
                .Replace("{merchant}", _options.LankaQrMerchantId, StringComparison.Ordinal)
                .Replace("{amount}", amount, StringComparison.Ordinal)
                .Replace("{reference}", reference, StringComparison.Ordinal);

        // The scannable fallback. Not an EMVCo TLV payload: the acquirer's real format is a
        // deployment fact D6' §7.2 does not print, and inventing a spec-shaped string would be
        // worse than an obviously-ours one that a bank app will refuse loudly.
        var payload = $"lankaqr:{_options.LankaQrMerchantId}:{amount}:{reference}";

        return Task.FromResult(new GatewaySession(null, null, link, payload));
    }
}

/// <summary>
/// <c>registry.driver_payouts</c> — D-11's OnePay merchant binding, read-only.
/// </summary>
/// <remarks>
/// registry-svc writes it when a vehicle reaches APPROVED (its `POST
/// /v1/internal/vehicles/{id}/merchant`). ADD §11.9 is explicit about the consequence of its
/// absence: "without successful merchant binding, fare-svc cannot route in-app payments for this
/// driver and falls back to cash by default".
/// </remarks>
internal interface IDriverPayoutRepository
{
    /// <summary>The driver's active merchant id, or <see langword="null"/> when they have none.</summary>
    Task<string?> ReadMerchantIdAsync(Guid driverId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDriverPayoutRepository"/>
internal sealed class DriverPayoutRepository(INpgsqlConnectionFactory connections) : IDriverPayoutRepository
{
    public async Task<string?> ReadMerchantIdAsync(Guid driverId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        // status = 'ACTIVE': a SUSPENDED binding is a merchant account the acquirer has closed, and
        // routing a fare into it would strand the money rather than pay the driver.
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            """
            SELECT onepay_merchant_id FROM registry.driver_payouts
             WHERE driver_id = @DriverId AND status = 'ACTIVE';
            """,
            new { DriverId = driverId },
            cancellationToken: cancellationToken));
    }
}
