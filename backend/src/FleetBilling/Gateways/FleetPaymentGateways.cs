using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MageRide.FleetBilling.Configuration;
using MageRide.FleetBilling.Domain;
using MageRide.Shared.Errors;
using Microsoft.Extensions.Options;

namespace MageRide.FleetBilling.Gateways;

/// <summary>What a gateway hands back for the Fleet Portal to open.</summary>
/// <param name="RedirectUrl">OnePay's hosted page (D6' §7.1).</param>
/// <param name="SessionToken">OnePay's session token, when it issues one.</param>
/// <param name="PaymentLink">AL-15's "Pay" deep link into the bank app.</param>
/// <param name="QrPayload">The LankaQR fallback, when the deployment has a payload template.</param>
internal sealed record GatewaySession(
    string? RedirectUrl, string? SessionToken, string? PaymentLink, string? QrPayload);

/// <summary>Starts a payment session at OnePay or at the LankaQR acquirer.</summary>
/// <remarks>
/// <b>Two rails and no third (AL-05).</b> ADD §6 gives fleet-billing-svc "top-up via
/// card/OnePay/LankaQR"; bank transfer was removed as a top-up method platform-wide, so there is no
/// implementation to switch on, no <c>method</c> value the database would accept
/// (<c>ck_fleet_topups_method</c>, migration 1108) and no manual reconciliation queue.
/// </remarks>
internal interface IFleetPaymentGateway
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
/// OnePay's create-session call (D6' §7.1), for a fleet wallet.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same request shape wallet-svc sends for a driver</b>, because it is the same gateway and
/// the same account: what differs is which of our wallets the callback credits, which is our fact
/// and not OnePay's. Held as its own class rather than shared with C046 because two services must
/// not reference each other's production assemblies, and the alternative — promoting the gateway
/// into the kernel — would put a payment client behind an interface every service takes a
/// dependency on for the sake of two callers.
/// </para>
/// <para>
/// <b>Nothing is credited here.</b> A session that returns successfully has moved no money; the
/// wallet is credited on the signed callback and never before.
/// </para>
/// </remarks>
internal sealed class OnepayFleetGateway(
    HttpClient client, IOptions<FleetBillingOptions> options, ILogger<OnepayFleetGateway> logger)
    : IFleetPaymentGateway
{
    /// <summary>The named client D6' §8.3's pipeline is attached to.</summary>
    public const string HttpClientName = "fleet-onepay";

    private readonly FleetBillingOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

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
                "OnePay is not configured on this deployment (Onepay:ApiKey / Onepay:BaseUrl). AL-05 leaves "
                + "LankaQR as the other rail; there is no bank-transfer fallback.");
        }

        try
        {
            using var response = await client.PostAsJsonAsync(
                "sessions",
                new
                {
                    orderId,
                    // Minor units, which is how the whole platform transmits money (CLAUDE.md). A
                    // gateway that wants rupees is a mapping to make once, here, and never a
                    // floating-point value travelling through the service.
                    amountMinor,
                    currency = "LKR",
                    returnUrl,
                    reference = topupId,
                },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "OnePay refused a session for fleet top-up {TopupId}: {Status}.", topupId, (int)response.StatusCode);

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
            logger.LogError(exception, "OnePay was unreachable for fleet top-up {TopupId}.", topupId);

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
/// The LankaQR rail (D6' §7.2, D-12, AL-15), for a fleet wallet.
/// </summary>
/// <remarks>
/// <b>No outbound call, and that is what AL-15 describes.</b> Both the deep link and the QR payload
/// are composed from the deployment's own templates plus the order reference — there is no session
/// to open with anybody, and the money arrives as a confirm callback. The QR payload is a template
/// and is never generated: an EMVCo TLV string's merchant fields and CRC belong to the acquiring
/// bank, and composing one here would put a plausible, unscannable code in front of an operator.
/// </remarks>
internal sealed class LankaQrFleetGateway(IOptions<FleetBillingOptions> options) : IFleetPaymentGateway
{
    private readonly FleetBillingOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

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
