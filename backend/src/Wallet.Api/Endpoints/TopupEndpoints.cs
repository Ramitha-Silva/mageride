using System.Text.Json;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Http;
using MageRide.Shared.Http.Idempotency;
using MageRide.Shared.Payments;
using MageRide.Wallet.Configuration;
using MageRide.Wallet.Gateways;
using MageRide.Wallet.Money;
using MageRide.Wallet.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace MageRide.Wallet.Endpoints;

/// <summary>
/// <c>/v1/wallet/topup/**</c> — the two rails AL-05 leaves, and their two HMAC callbacks.
/// </summary>
/// <remarks>
/// <para>
/// <b>The wallet is credited on the callback and never on the initiate.</b> A session the gateway
/// accepted has moved no money; treating it as a credit is how a balance grows by abandoning a payment
/// page. D3' says the same thing — "on webhook → balanced double-entry journal credit".
/// </para>
/// <para>
/// <b>There is no bank-transfer route, and there is no third rail to add one to.</b> AL-05 removed it;
/// the contract's header lists it under "REMOVED, do not re-add", and <c>ck_topups_method</c> (1107)
/// would refuse the row.
/// </para>
/// </remarks>
public static class TopupEndpoints
{
    public static IEndpointRouteBuilder MapTopupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var topup = endpoints.MapGroup("/v1/wallet/topup").WithTags("topup");

        // Bearer + driver: a top-up puts money in the caller's own wallet, so there is no {userId} to
        // scope — the token is the wallet.
        topup.MapPost("/onepay", StartOnepayAsync)
            .RequireMageRideRole(MageRideRoles.Driver, MageRideRoles.FleetOwner)
            .WithName("topupWithOnepay");

        topup.MapPost("/lankaqr", StartLankaQrAsync)
            .RequireMageRideRole(MageRideRoles.Driver, MageRideRoles.FleetOwner)
            .WithName("topupWithLankaqr");

        topup.MapGet("/{topupId}", GetTopupAsync)
            .RequireAuthorization()
            .WithName("getTopup");

        // The two provider callbacks. AllowAnonymous because a gateway presents no bearer; the HMAC
        // signature is what authenticates them (D6' §7.1/§7.2), and the kernel's deny-by-default
        // fallback policy would otherwise 401 before the signature was read. Idempotency-exempt because
        // a provider cannot send our header — they dedupe on `provider_transaction_id` (R-19).
        topup.MapPost("/onepay/webhook", OnepayWebhookAsync)
            .AllowAnonymous()
            .AllowMissingIdempotencyKey()
            .WithName("onepayTopupWebhook");

        topup.MapPost("/lankaqr/confirm", LankaQrConfirmAsync)
            .AllowAnonymous()
            .AllowMissingIdempotencyKey()
            .WithName("lankaqrTopupConfirm");

        return endpoints;
    }

    private static Task<Ok<TopupResponse>> StartOnepayAsync(
        TopupRequestBody? body, HttpContext context, TopupService topups, CancellationToken cancellationToken) =>
        StartAsync(TopupMethods.Onepay, body, context, topups, cancellationToken);

    private static Task<Ok<TopupResponse>> StartLankaQrAsync(
        TopupRequestBody? body, HttpContext context, TopupService topups, CancellationToken cancellationToken) =>
        StartAsync(TopupMethods.LankaQr, body, context, topups, cancellationToken);

    private static async Task<Ok<TopupResponse>> StartAsync(
        string method,
        TopupRequestBody? body,
        HttpContext context,
        TopupService topups,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(topups);

        if (body?.AmountMinor is not { } amountMinor)
        {
            throw new MageRideException(MageRideErrors.InvalidAmount, "amountMinor is required.");
        }

        var (topup, session) = await topups.StartAsync(
            context.User.RequireSubjectId(), method, amountMinor, body.ReturnUrl, cancellationToken);

        return TypedResults.Ok(new TopupResponse(
            topup.Id,
            topup.State,
            topup.AmountMinor,
            topup.Currency,
            session.RedirectUrl,
            session.SessionToken,
            session.PaymentLink,
            session.QrPayload));
    }

    /// <remarks>
    /// The session artefacts are deliberately absent from this answer: they are single-use instruments of
    /// a gateway session and are not stored (see <c>TopupService.StartAsync</c>). What a polling client
    /// needs is the state.
    /// </remarks>
    private static async Task<Ok<TopupResponse>> GetTopupAsync(
        string topupId,
        HttpContext context,
        ITopupRepository topups,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(topups);

        var id = RequestIds.Require(topupId, "topupId");
        var topup = await topups.ReadAsync(id, cancellationToken);

        // Not the caller's session and no such session are the same answer: telling them apart would
        // make this an oracle over other drivers' payments.
        if (topup is null || topup.DriverId != context.User.RequireSubjectId())
        {
            throw new MageRideException(MageRideErrors.NotFound, "No such top-up.");
        }

        return TypedResults.Ok(new TopupResponse(
            topup.Id, topup.State, topup.AmountMinor, topup.Currency, null, null, null, null));
    }

    private static Task<Ok<CallbackAcceptedResponse>> OnepayWebhookAsync(
        HttpContext context,
        TopupService topups,
        IOptions<WalletOptions> options,
        ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        HandleCallbackAsync(
            TopupMethods.Onepay,
            options.Value.OnepayWebhookSecret,
            context,
            topups,
            loggers,
            cancellationToken);

    private static Task<Ok<CallbackAcceptedResponse>> LankaQrConfirmAsync(
        HttpContext context,
        TopupService topups,
        IOptions<WalletOptions> options,
        ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        HandleCallbackAsync(
            TopupMethods.LankaQr,
            options.Value.LankaQrWebhookSecret,
            context,
            topups,
            loggers,
            cancellationToken);

    /// <summary>
    /// Verifies the signature over the <b>raw</b> body, then settles.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The raw bytes, before any parsing</b> (<c>_shared.yaml</c>: "verified before any body
    /// parsing"). Deserialising and re-serialising changes whitespace and key order, so the digest of a
    /// round-tripped body is not the digest the provider signed.
    /// </para>
    /// <para>
    /// <b>Unsigned is refused, and an unset secret refuses everything.</b> There is no "accept unsigned"
    /// mode: a wallet-credit endpoint that trusts an unsigned body is a free-money endpoint. A
    /// deployment with no secret configured therefore credits nothing at all — which is why
    /// <c>WalletApplication</c> says so loudly at start-up.
    /// </para>
    /// <para>
    /// A redelivery answers <c>200</c> with the same body as the first delivery, because that is what
    /// stops a provider retrying for ever (the contract says so explicitly).
    /// </para>
    /// </remarks>
    private static async Task<Ok<CallbackAcceptedResponse>> HandleCallbackAsync(
        string method,
        string? secret,
        HttpContext context,
        TopupService topups,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(topups);
        ArgumentNullException.ThrowIfNull(loggers);

        var logger = loggers.CreateLogger(typeof(TopupEndpoints));

        context.Request.EnableBuffering();

        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, cancellationToken);
        var raw = buffer.ToArray();
        context.Request.Body.Position = 0;

        var presented = context.Request.Headers[WebhookSignature.HeaderName].ToString();

        if (!WebhookSignature.IsValid(raw, presented, secret))
        {
            logger.LogWarning(
                "A {Method} top-up callback arrived with an invalid or missing {Header} and was refused. "
                + "Secret configured: {HasSecret}.",
                method,
                WebhookSignature.HeaderName,
                !string.IsNullOrWhiteSpace(secret));

            throw new MageRideException(
                MageRideErrors.Unauthorized, "The callback signature could not be verified.");
        }

        var body = JsonSerializer.Deserialize<TopupCallbackBody>(raw, MageRideJson.Options)
                   ?? throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
                   {
                       ["body"] = ["The callback body is empty."],
                   });

        await topups.SettleAsync(
            method,
            new TopupCallback(
                body.ProviderTransactionId ?? string.Empty,
                RequestIds.Optional(body.TopupId),
                body.OrderId,
                body.Status ?? string.Empty,
                body.AmountMinor),
            cancellationToken);

        return TypedResults.Ok(new CallbackAcceptedResponse(true));
    }
}
