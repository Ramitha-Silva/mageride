using System.Text.Json;
using MageRide.Fare.Configuration;
using MageRide.Fare.Domain;
using MageRide.Fare.Gateways;
using MageRide.Fare.Payments;
using MageRide.Fare.Persistence;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Http;
using MageRide.Shared.Http.Idempotency;
using MageRide.Shared.Payments;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace MageRide.Fare.Endpoints;

// =============================================================================================
// The wire shapes of backend/contracts/fare.yaml's payment surface. The contract wins over this
// file: it is what C012/C013 generate the KMP client from and what C118 asserts the service
// against.
// =============================================================================================

/// <summary>`POST /v1/fare/pay`.</summary>
public sealed record InitiatePaymentBody(string? RideId, string? Method, long? TipMinor);

/// <summary>`POST /v1/fare/pay/scan-driver-qr` (AL-22).</summary>
public sealed record ScanDriverQrBody(string? RideId, string? QrPayload);

/// <summary>`POST /v1/fare/pay/driver-qr/claim` (AL-47).</summary>
public sealed record DriverQrClaimBody(string? RideId, string? ReceiptArtifactId);

/// <summary>`POST /v1/fare/pay/driver-qr/confirm` and `/dispute`.</summary>
public sealed record DriverQrBody(string? RideId, string? Note);

/// <summary>`POST /v1/admin/fare/refund` (E-05).</summary>
public sealed record RefundFareBody(string? PaymentId, string? Kind, long? AmountMinor, string? ReasonCode);

/// <summary>The gateway artefacts one method hands back; the other block is always absent.</summary>
public sealed record OnepayBlock(string? RedirectUrl, string? SessionToken);

/// <summary>AL-15: the deep link is primary and the QR is the fallback.</summary>
public sealed record LankaQrBlock(string? QrPayload, string? PaymentLink);

/// <summary>The 200 of `POST /v1/fare/pay`.</summary>
public sealed record PaymentInitiationResponse(
    Guid PaymentId,
    string State,
    string Method,
    long AmountMinor,
    long SurchargeMinor,
    string Currency,
    OnepayBlock? Onepay,
    LankaQrBlock? LankaQr);

/// <summary>The shape every other payment operation answers with.</summary>
public sealed record PaymentStatusResponse(
    Guid PaymentId,
    Guid RideId,
    string State,
    string Method,
    long AmountMinor,
    long SurchargeMinor,
    long TipMinor,
    string Currency,
    DateTimeOffset? SettledAt)
{
    public static PaymentStatusResponse From(RidePayment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);

        return new PaymentStatusResponse(
            payment.Id,
            payment.RideId,
            payment.State,
            payment.Method,
            payment.AmountMinor,
            payment.SurchargeMinor,
            payment.TipAmountMinor,
            payment.Currency,
            // The instant the money stopped moving. Only the driver-QR path records one of its own;
            // for everything else the payment's own row carries no settlement column, so the field
            // is absent rather than filled with `updated_at`, which is not the same fact.
            payment.QrConfirmedAt);
    }
}

/// <summary>`POST /v1/fare/pay/driver-qr/dispute`.</summary>
public sealed record DisputeTicketResponse(Guid TicketId);

/// <summary>The 201 of `POST /v1/admin/fare/refund`.</summary>
public sealed record RefundResponse(Guid RefundId, string Status, long AmountMinor, string Currency);

/// <summary>What a provider callback is answered with, first delivery and redelivery alike.</summary>
public sealed record CallbackAcceptedResponse(bool Received);

/// <summary>
/// <c>/v1/fare/pay/**</c> and <c>/v1/admin/fare/refund</c> — D-10's machine, every settlement path.
/// </summary>
public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var pay = endpoints.MapGroup("/v1/fare/pay").WithTags("payments").RequireAuthorization();

        // The two provider callbacks. AllowAnonymous because a gateway presents no bearer — the HMAC
        // signature over the raw body is what authenticates them (D6' §7.1/§7.2) — and
        // idempotency-exempt because an external gateway cannot send our header; they dedupe on
        // provider_transaction_id (R-19).
        pay.MapPost("/onepay/webhook", OnepayWebhookAsync)
            .AllowAnonymous()
            .AllowMissingIdempotencyKey()
            .WithName("onepayPaymentWebhook");

        pay.MapPost("/lankaqr/confirm", LankaQrConfirmAsync)
            .AllowAnonymous()
            .AllowMissingIdempotencyKey()
            .WithName("lankaqrPaymentConfirm");

        pay.MapPost("/scan-driver-qr", ScanDriverQrAsync).WithTags("driver-qr").WithName("payByScanningDriverQr");

        pay.MapPost("/driver-qr/claim", ClaimAsync).WithTags("driver-qr").WithName("claimDriverQrPayment");
        pay.MapPost("/driver-qr/confirm", ConfirmAsync).WithTags("driver-qr").WithName("confirmDriverQrPayment");
        pay.MapPost("/driver-qr/dispute", DisputeAsync).WithTags("driver-qr").WithName("disputeDriverQrPayment");

        pay.MapGet("/{paymentId}/status", StatusAsync).WithName("getPaymentStatus");
        pay.MapPost("/{paymentId}/fallback-cash", FallbackCashAsync).WithName("fallbackToCash");

        pay.MapPost(string.Empty, InitiateAsync).WithName("initiatePayment");

        // E-05. Finance and the two blanket admin roles; the D-35 audit row for it is admin-bff's,
        // by the same split C045, C046 and C047 use.
        endpoints.MapPost("/v1/admin/fare/refund", RefundAsync)
            .RequireMageRideRole(MageRideRoles.FinanceOfficer, MageRideRoles.Admin, MageRideRoles.SuperAdmin)
            .WithTags("fare-admin")
            .WithName("refundFare");

        return endpoints;
    }

    private static async Task<Ok<PaymentInitiationResponse>> InitiateAsync(
        InitiatePaymentBody? body,
        HttpContext context,
        PaymentService payments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payments);

        var initiated = await payments.PayAsync(
            context.User.RequireSubjectId(),
            RequestIds.Require(body?.RideId, "rideId"),
            body?.Method?.Trim() ?? string.Empty,
            body?.TipMinor ?? 0,
            cancellationToken);

        var payment = initiated.Payment;
        var session = initiated.Session;

        return TypedResults.Ok(new PaymentInitiationResponse(
            payment.Id,
            payment.State,
            payment.Method,
            payment.AmountMinor,
            payment.SurchargeMinor,
            payment.Currency,
            payment.Method == RidePaymentMethods.Onepay
                ? new OnepayBlock(session.RedirectUrl, session.SessionToken)
                : null,
            payment.Method == RidePaymentMethods.LankaQr
                ? new LankaQrBlock(session.QrPayload, session.PaymentLink)
                : null));
    }

    private static async Task<Ok<PaymentStatusResponse>> StatusAsync(
        string paymentId, HttpContext context, PaymentService payments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payments);

        var payment = await payments.StatusAsync(
            context.User.RequireSubjectId(), RequestIds.Require(paymentId, "paymentId"), cancellationToken);

        return TypedResults.Ok(PaymentStatusResponse.From(payment));
    }

    private static async Task<Ok<PaymentStatusResponse>> FallbackCashAsync(
        string paymentId, HttpContext context, PaymentService payments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payments);

        var payment = await payments.FallbackToCashAsync(
            context.User.RequireSubjectId(), RequestIds.Require(paymentId, "paymentId"), cancellationToken);

        return TypedResults.Ok(PaymentStatusResponse.From(payment));
    }

    private static async Task<Ok<PaymentStatusResponse>> ScanDriverQrAsync(
        ScanDriverQrBody? body, HttpContext context, PaymentService payments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payments);

        var payment = await payments.ScanDriverQrAsync(
            context.User.RequireSubjectId(),
            RequestIds.Require(body?.RideId, "rideId"),
            body?.QrPayload ?? string.Empty,
            cancellationToken);

        return TypedResults.Ok(PaymentStatusResponse.From(payment));
    }

    /// <remarks>202, not 200: the claim is recorded and the driver prompted, and nothing is settled
    /// until they answer. The contract says so.</remarks>
    private static async Task<Accepted<PaymentStatusResponse>> ClaimAsync(
        DriverQrClaimBody? body, HttpContext context, DriverQrService driverQr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(driverQr);

        var rideId = RequestIds.Require(body?.RideId, "rideId");

        var payment = await driverQr.ClaimAsync(
            context.User.RequireSubjectId(),
            rideId,
            RequestIds.Optional(body?.ReceiptArtifactId),
            cancellationToken);

        return TypedResults.Accepted(
            $"/v1/fare/pay/{payment.Id}/status", PaymentStatusResponse.From(payment));
    }

    private static async Task<Ok<PaymentStatusResponse>> ConfirmAsync(
        DriverQrBody? body, HttpContext context, DriverQrService driverQr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(driverQr);

        var payment = await driverQr.ConfirmAsync(
            context.User.RequireSubjectId(), RequestIds.Require(body?.RideId, "rideId"), cancellationToken);

        return TypedResults.Ok(PaymentStatusResponse.From(payment));
    }

    private static async Task<Created<DisputeTicketResponse>> DisputeAsync(
        DriverQrBody? body, HttpContext context, DriverQrService driverQr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(driverQr);

        var ticketId = await driverQr.DisputeAsync(
            context.User.RequireSubjectId(),
            RequestIds.Require(body?.RideId, "rideId"),
            body?.Note,
            cancellationToken);

        return TypedResults.Created($"/v1/support/tickets/{ticketId}", new DisputeTicketResponse(ticketId));
    }

    private static async Task<Created<RefundResponse>> RefundAsync(
        RefundFareBody? body, HttpContext context, RefundService refunds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(refunds);

        if (body?.AmountMinor is not { } amountMinor)
        {
            throw new MageRideException(MageRideErrors.InvalidAmount, "amountMinor is required.");
        }

        var reasonCode = body.ReasonCode?.Trim();

        if (string.IsNullOrEmpty(reasonCode))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["reasonCode"] = ["reasonCode is required — Finance reconciles refunds by it."],
            });
        }

        var refund = await refunds.RefundAsync(
            context.User.RequireSubjectId(),
            RequestIds.Require(body.PaymentId, "paymentId"),
            body.Kind?.Trim() ?? string.Empty,
            amountMinor,
            reasonCode,
            cancellationToken);

        return TypedResults.Created(
            $"/v1/admin/fare/refund/{refund.Id}",
            new RefundResponse(refund.Id, refund.Status, refund.AmountMinor, refund.Currency));
    }

    // -----------------------------------------------------------------------------------------
    // Provider callbacks
    // -----------------------------------------------------------------------------------------

    private static Task<Ok<CallbackAcceptedResponse>> OnepayWebhookAsync(
        HttpContext context,
        CallbackService callbacks,
        IOptions<FareOptions> options,
        ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        HandleCallbackAsync(
            RidePaymentMethods.Onepay, options?.Value.OnepayWebhookSecret, context, callbacks, loggers, cancellationToken);

    private static Task<Ok<CallbackAcceptedResponse>> LankaQrConfirmAsync(
        HttpContext context,
        CallbackService callbacks,
        IOptions<FareOptions> options,
        ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        HandleCallbackAsync(
            RidePaymentMethods.LankaQr, options?.Value.LankaQrWebhookSecret, context, callbacks, loggers, cancellationToken);

    /// <summary>Verifies the signature over the <b>raw</b> body, then settles.</summary>
    /// <remarks>
    /// <b>Unsigned is refused, and an unset secret refuses everything.</b> A callback that settles a
    /// fare without a signature is a free-ride endpoint for anyone who finds the URL — and on the
    /// late-callback path it would also mint a refund out of a settled cash ride.
    /// </remarks>
    private static async Task<Ok<CallbackAcceptedResponse>> HandleCallbackAsync(
        string method,
        string? secret,
        HttpContext context,
        CallbackService callbacks,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(callbacks);
        ArgumentNullException.ThrowIfNull(loggers);

        var logger = loggers.CreateLogger(typeof(PaymentEndpoints));

        context.Request.EnableBuffering();

        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, cancellationToken);
        var raw = buffer.ToArray();
        context.Request.Body.Position = 0;

        var presented = context.Request.Headers[WebhookSignature.HeaderName].ToString();

        if (!WebhookSignature.IsValid(raw, presented, secret))
        {
            logger.LogWarning(
                "A {Method} payment callback arrived with an invalid or missing {Header} and was refused. "
                + "Secret configured: {HasSecret}.",
                method,
                WebhookSignature.HeaderName,
                !string.IsNullOrWhiteSpace(secret));

            throw new MageRideException(
                MageRideErrors.Unauthorized, "The callback signature could not be verified.");
        }

        var body = JsonSerializer.Deserialize<ProviderCallbackBody>(raw, MageRideJson.Options)
                   ?? throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
                   {
                       ["body"] = ["The callback body is empty."],
                   });

        if (string.IsNullOrWhiteSpace(body.ProviderTransactionId))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["providerTransactionId"] = ["providerTransactionId is required — it is the R-19 dedupe key."],
            });
        }

        await callbacks.HandleAsync(
            new ProviderCallback(
                body.ProviderTransactionId,
                RequestIds.Optional(body.PaymentId),
                body.Status ?? string.Empty,
                body.AmountMinor),
            cancellationToken);

        return TypedResults.Ok(new CallbackAcceptedResponse(true));
    }

    /// <summary>The wire shape of <c>fare.yaml</c>'s <c>ProviderCallback</c>.</summary>
    private sealed record ProviderCallbackBody(
        string? ProviderTransactionId, string? PaymentId, string? Status, long? AmountMinor);
}
