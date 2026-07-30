using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MageRide.Shared.Errors;
using MageRide.Wallet.Configuration;
using MageRide.Wallet.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.Wallet.Gateways;

/// <summary>What a gateway hands back for the app to open.</summary>
/// <param name="RedirectUrl">OnePay's hosted page (D6' §7.1).</param>
/// <param name="SessionToken">OnePay's session token, when it issues one.</param>
/// <param name="PaymentLink">AL-15's "Pay" deep link into the bank app.</param>
/// <param name="QrPayload">The LankaQR fallback, when the deployment has a payload template.</param>
internal sealed record GatewaySession(
    string? RedirectUrl, string? SessionToken, string? PaymentLink, string? QrPayload);

/// <summary>Starts a payment session at OnePay or at the LankaQR acquirer.</summary>
internal interface IPaymentGateway
{
    /// <summary>Which top-up method this gateway serves (<see cref="TopupMethods"/>).</summary>
    string Method { get; }

    /// <summary>Whether the deployment has configured it. False makes a top-up answer <c>503</c>.</summary>
    bool IsConfigured { get; }

    /// <summary>Opens a session for one top-up.</summary>
    Task<GatewaySession> StartAsync(
        Guid topupId, string orderId, long amountMinor, string? returnUrl, CancellationToken cancellationToken);
}

/// <summary>
/// OnePay's create-session call (D6' §7.1).
/// </summary>
/// <remarks>
/// <para>
/// One downstream, no fallback provider. D6' §7.1's fallback for a *ride* is cash; for a wallet top-up
/// it is "OnePay/LankaQR only — no bank-transfer fallback (AL-05)", so a failed session is a
/// <c>503</c> and the driver picks the other rail themselves.
/// </para>
/// <para>
/// <b>The response shape is the one D6' §7.1 prints</b> — <c>{redirectUrl|sessionToken}</c> — and
/// nothing more is read from it. The wallet is credited on the callback, never here: a session that
/// returns successfully has moved no money, and treating it as a credit is how a driver's balance grows
/// by abandoning a payment page.
/// </para>
/// </remarks>
internal sealed class OnepayGateway(
    HttpClient client, IOptions<WalletOptions> options, ILogger<OnepayGateway> logger) : IPaymentGateway
{
    private readonly WalletOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public string Method => TopupMethods.Onepay;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.OnepayApiKey) && !string.IsNullOrWhiteSpace(_options.OnepayBaseUrl);

    public async Task<GatewaySession> StartAsync(
        Guid topupId, string orderId, long amountMinor, string? returnUrl, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new MageRideException(
                MageRideErrors.DependencyUnavailable,
                "OnePay is not configured on this deployment (Onepay:ApiKey / Onepay:BaseUrl).");
        }

        try
        {
            using var response = await client.PostAsJsonAsync(
                "sessions",
                new
                {
                    orderId,
                    // The gateway is told the amount in minor units, which is how the whole platform
                    // transmits money (CLAUDE.md). A gateway that wants rupees is a mapping to make
                    // once, here, and not a floating-point value travelling through the service.
                    amountMinor,
                    currency = "LKR",
                    returnUrl,
                    reference = topupId,
                },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "OnePay refused a session for top-up {TopupId}: {Status}.", topupId, (int)response.StatusCode);

                throw new MageRideException(
                    MageRideErrors.GatewayError, $"OnePay returned {(int)response.StatusCode}.");
            }

            var session = await response.Content.ReadFromJsonAsync<OnepaySessionResponse>(cancellationToken);

            if (session is null || (string.IsNullOrWhiteSpace(session.RedirectUrl)
                                    && string.IsNullOrWhiteSpace(session.SessionToken)))
            {
                throw new MageRideException(
                    MageRideErrors.GatewayError,
                    "OnePay accepted the session but returned neither a redirect URL nor a session token.");
            }

            return new GatewaySession(session.RedirectUrl, session.SessionToken, null, null);
        }
        catch (HttpRequestException exception)
        {
            logger.LogError(exception, "OnePay was unreachable for top-up {TopupId}.", topupId);

            throw new MageRideException(
                MageRideErrors.DependencyUnavailable, "OnePay is unreachable.", exception);
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
/// The LankaQR rail (D6' §7.2, D-12, AL-15).
/// </summary>
/// <remarks>
/// <para>
/// <b>No outbound call, and that is what AL-15 describes.</b> "Returns a 'Pay' deep link into the bank
/// app; the QR payload is the fallback for devices where the deep link does not resolve." Both are
/// composed from the deployment's own templates plus the order reference — there is no session to open
/// with anybody, and the money arrives as a confirm callback on
/// <c>/v1/wallet/topup/lankaqr/confirm</c>.
/// </para>
/// <para>
/// <b>The QR payload is a template and is never generated.</b> A LankaQR payload is an EMVCo TLV
/// string whose merchant fields and CRC belong to the acquiring bank; composing one here would put a
/// plausible, unscannable code in front of a driver, which is worse than not offering the fallback at
/// all. Unset, <c>qrPayload</c> is simply omitted.
/// </para>
/// </remarks>
internal sealed class LankaQrGateway(IOptions<WalletOptions> options) : IPaymentGateway
{
    private readonly WalletOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public string Method => TopupMethods.LankaQr;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.LankaQrDeepLinkTemplate);

    public Task<GatewaySession> StartAsync(
        Guid topupId, string orderId, long amountMinor, string? returnUrl, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new MageRideException(
                MageRideErrors.DependencyUnavailable,
                "LankaQR is not configured on this deployment (LankaQr:DeepLinkTemplate). AL-15 makes the "
                + "bank-app deep link the primary path, so there is nothing to fall back to.");
        }

        return Task.FromResult(new GatewaySession(
            RedirectUrl: null,
            SessionToken: null,
            PaymentLink: Fill(_options.LankaQrDeepLinkTemplate!, orderId, amountMinor),
            QrPayload: string.IsNullOrWhiteSpace(_options.LankaQrPayloadTemplate)
                ? null
                : Fill(_options.LankaQrPayloadTemplate, orderId, amountMinor)));
    }

    private string Fill(string template, string orderId, long amountMinor) =>
        template
            .Replace("{orderId}", Uri.EscapeDataString(orderId), StringComparison.Ordinal)
            .Replace(
                "{amountMinor}",
                amountMinor.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            .Replace(
                "{merchantId}",
                Uri.EscapeDataString(_options.LankaQrMerchantId ?? string.Empty),
                StringComparison.Ordinal);
}
