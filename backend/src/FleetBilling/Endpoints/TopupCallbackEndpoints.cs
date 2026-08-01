using System.Text.Json;
using MageRide.FleetBilling.Configuration;
using MageRide.FleetBilling.Domain;
using MageRide.FleetBilling.Money;
using MageRide.Shared.Errors;
using MageRide.Shared.Http;
using MageRide.Shared.Http.Idempotency;
using MageRide.Shared.Payments;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace MageRide.FleetBilling.Endpoints;

/// <summary>
/// <c>/v1/fleet-billing/topup/onepay/webhook</c> and <c>/lankaqr/confirm</c> — where the money
/// actually arrives.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not under <c>/v1/fleets/{fleetId}</c>, and it could not be.</b> A payment provider knows an
/// order reference and a transaction id; it does not know which MageRide organisation the session
/// belonged to, and putting a fleet id in the path would mean either trusting the provider to supply
/// one or publishing a URL per organisation. The session's own row is what resolves the fleet
/// (R-19's dedupe key, then our echoed <c>orderId</c>).
/// </para>
/// <para>
/// <b>The signature is verified over the raw bytes, before any parsing, and there is no unsigned
/// mode.</b> A wallet-credit endpoint that trusts an unsigned body is a free-money endpoint — and
/// this one credits an organisation that owes the platform money, so a forged callback would settle
/// an invoice for nothing. A deployment with no secret configured credits nothing at all, which is
/// why <c>FleetBillingApplication</c> says so loudly at start-up.
/// </para>
/// <para>
/// <b>A redelivery answers 200 with the same body.</b> That is what stops a provider retrying for
/// ever, and the R-19 guard (<c>ux_fleet_topups_provider_txn</c>) is what makes it safe: nothing is
/// credited twice.
/// </para>
/// </remarks>
public static class TopupCallbackEndpoints
{
    /// <summary>The prefix the two provider callbacks live under.</summary>
    public const string CallbackGroup = "/v1/fleet-billing/topup";

    public static IEndpointRouteBuilder MapTopupCallbackEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // AllowAnonymous because a gateway presents no bearer — the HMAC signature is what
        // authenticates it (D6' §7.1/§7.2) — and the kernel's deny-by-default fallback policy would
        // otherwise 401 before the signature was read. Idempotency-exempt because a provider cannot
        // send our header; they dedupe on `provider_transaction_id` (R-19).
        var callbacks = endpoints.MapGroup(CallbackGroup)
            .WithTags("fleet-billing")
            .AllowAnonymous()
            .AllowMissingIdempotencyKey();

        callbacks.MapPost("/onepay/webhook", OnepayWebhookAsync).WithName("onepayFleetTopupWebhook");
        callbacks.MapPost("/lankaqr/confirm", LankaQrConfirmAsync).WithName("lankaqrFleetTopupConfirm");

        return endpoints;
    }

    private static Task<Ok<TopupCallbackResponse>> OnepayWebhookAsync(
        HttpContext context,
        IFleetTopupService topups,
        IOptions<FleetBillingOptions> options,
        ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        HandleAsync(
            TopupMethods.Onepay,
            options.Value.OnepayWebhookSecret,
            context,
            topups,
            loggers,
            cancellationToken);

    private static Task<Ok<TopupCallbackResponse>> LankaQrConfirmAsync(
        HttpContext context,
        IFleetTopupService topups,
        IOptions<FleetBillingOptions> options,
        ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        HandleAsync(
            TopupMethods.LankaQr,
            options.Value.LankaQrWebhookSecret,
            context,
            topups,
            loggers,
            cancellationToken);

    private static async Task<Ok<TopupCallbackResponse>> HandleAsync(
        string method,
        string? secret,
        HttpContext context,
        IFleetTopupService topups,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(topups);
        ArgumentNullException.ThrowIfNull(loggers);

        var logger = loggers.CreateLogger(typeof(TopupCallbackEndpoints));

        context.Request.EnableBuffering();

        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, cancellationToken);
        var raw = buffer.ToArray();
        context.Request.Body.Position = 0;

        var presented = context.Request.Headers[WebhookSignature.HeaderName].ToString();

        if (!WebhookSignature.IsValid(raw, presented, secret))
        {
            logger.LogWarning(
                "A {Method} fleet top-up callback arrived with an invalid or missing {Header} and was "
                + "refused. Secret configured: {HasSecret}.",
                method,
                WebhookSignature.HeaderName,
                !string.IsNullOrWhiteSpace(secret));

            throw new MageRideException(
                MageRideErrors.Unauthorized, "The callback signature could not be verified.");
        }

        var body = JsonSerializer.Deserialize<TopupCallbackBody>(raw, MageRideJson.Options)
                   ?? throw new MageRideValidationException(
                       new Dictionary<string, string[]>(StringComparer.Ordinal)
                       {
                           ["body"] = ["The callback body is empty."],
                       });

        var settlement = await topups.SettleAsync(
            method,
            new FleetTopupCallback(
                body.ProviderTransactionId ?? string.Empty,
                RequestIds.Optional(body.TopupId),
                body.OrderId,
                body.Status ?? string.Empty,
                body.AmountMinor),
            cancellationToken);

        return TypedResults.Ok(new TopupCallbackResponse(
            settlement.Topup.Id, settlement.Topup.State, settlement.Credited, settlement.Replayed));
    }
}
