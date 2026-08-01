using System.Text.Json;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Http;
using MageRide.Shared.Http.Idempotency;
using MageRide.Shared.Payments;
using MageRide.Shared.Primitives;
using MageRide.Subscriptions.Configuration;
using MageRide.Subscriptions.Domain;
using MageRide.Subscriptions.ModeB;
using MageRide.Subscriptions.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace MageRide.Subscriptions.Endpoints;

/// <summary>
/// <c>/v1/mode-b/**</c> — Epic 23: per-vehicle access requests, the subscriptions an accept starts,
/// and the subscriber payments that pass through to the fleet owner.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three audiences on one prefix.</b> A passenger reaches their own requests, subscriptions and
/// payments; a driver or fleet Manager reaches one vehicle's queue and roster; the Owner alone
/// reaches the money. The rule is never a role claim — <c>fleet_owner</c> says somebody runs *a*
/// fleet, not *this* vehicle — it is always resolved against the vehicle, in
/// <see cref="ModeBVehicle"/>, by the same query that fetched it.
/// </para>
/// <para>
/// <b>The Fleet Portal does not call these paths directly.</b> D3' gives fleet-svc an org-scoped
/// proxy (<c>/v1/fleets/{fleetId}/vehicles/{vehicleId}/…</c>, C059) over exactly this surface. Both
/// spellings resolve to the same rows and the same checks; the proxy adds the org scope, which is
/// why the fleet id never appears in a path here.
/// </para>
/// </remarks>
public static class ModeBEndpoints
{
    public static IEndpointRouteBuilder MapModeBEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var modeB = endpoints.MapGroup("/v1/mode-b").WithTags("mode-b").RequireAuthorization();

        // Literal segments before templates: `{vehicleId}` would otherwise swallow `subscriptions`,
        // `payments`, `pay` and `files` and fail them as malformed ULIDs.
        modeB.MapPost("/access-requests/{requestId}/accept", AcceptAsync).WithName("acceptModeBAccessRequest");
        modeB.MapPost("/access-requests/{requestId}/reject", RejectAsync).WithName("rejectModeBAccessRequest");

        modeB.MapGet("/subscriptions/{passengerId}", ListSubscriptionsAsync).WithName("listPassengerSubscriptions");
        modeB.MapPost("/subscriptions/{subscriptionId}/unsubscribe", UnsubscribeAsync).WithName("unsubscribeModeB");
        modeB.MapPost("/subscriptions/{subscriptionId}/pay", PayAsync).WithName("payModeBSubscription");
        modeB.MapGet("/subscriptions/{subscriptionId}/payments", ListMyPaymentsAsync)
            .WithName("listSubscriptionPayments");

        modeB.MapPost("/payments/{paymentId}/transfer-slip", UploadSlipAsync)
            .WithName("uploadTransferSlip")
            // The same reason ride-svc's proof photo and provisioning-svc's bulk upload disable it:
            // the request is a bearer-authenticated API call from an app, not a browser form post,
            // so there is no cookie for a cross-site request to ride on and no token to carry.
            .DisableAntiforgery();

        modeB.MapPost("/payments/{paymentId}/confirm", ConfirmSlipAsync).WithName("confirmTransferSlip");

        // The two provider callbacks. AllowAnonymous because a gateway presents no bearer — the HMAC
        // signature over the raw body is what authenticates them (D6' §7.1/§7.2) — and
        // idempotency-exempt because an external gateway cannot send our header; they dedupe on
        // provider_transaction_id (R-19).
        modeB.MapPost("/pay/onepay/webhook", OnepayWebhookAsync)
            .AllowAnonymous()
            .AllowMissingIdempotencyKey()
            .WithName("modeBOnepayWebhook");

        modeB.MapPost("/pay/lankaqr/confirm", LankaQrConfirmAsync)
            .AllowAnonymous()
            .AllowMissingIdempotencyKey()
            .WithName("modeBLankaqrConfirm");

        // Signed, expiring document links. Not in D3' — it asks for "a signed URL" and gives it no
        // route — so this is the C048 micro-change-set the handoff records, the same shape
        // provisioning-svc's `errors.csv` took. Anonymous because the signature is the credential and
        // an image loader carries no bearer.
        modeB.MapGet("/files/{kind}/{id}", GetFileAsync).AllowAnonymous().WithName("getModeBFile");

        modeB.MapPost("/{vehicleId}/access-requests", RequestAccessAsync).WithName("requestModeBAccess");
        modeB.MapGet("/{vehicleId}/access-requests", ListRequestsAsync).WithName("listModeBAccessRequests");

        modeB.MapGet("/{vehicleId}/subscribers", ListRosterAsync).WithName("listModeBSubscribers");
        modeB.MapDelete("/{vehicleId}/subscribers/{subscriberId}", DeleteSubscriberAsync)
            .WithName("deleteModeBSubscriber");
        modeB.MapPut("/{vehicleId}/subscribers/{subscriberId}/fare", SetFareAsync).WithName("setSubscriberFare");
        modeB.MapPost("/{vehicleId}/subscribers/{subscriberId}/mark-cash", MarkCashAsync)
            .WithName("markSubscriberCashPaid");
        modeB.MapGet("/{vehicleId}/subscribers/{subscriberId}/payments", ListSubscriberPaymentsAsync)
            .WithName("listSubscriberPayments");

        return endpoints;
    }

    // -----------------------------------------------------------------------------------------
    // Access requests
    // -----------------------------------------------------------------------------------------

    private static async Task<Created<AccessRequestResponse>> RequestAccessAsync(
        string vehicleId,
        RequestModeBAccessBody? body,
        HttpContext context,
        ModeBAccessService access,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(access);

        var vehicle = RequestIds.Require(vehicleId, "vehicleId");
        var passenger = context.User.RequireSubjectId();

        var request = await access.RequestAccessAsync(passenger, vehicle, cancellationToken);

        return TypedResults.Created(
            $"/v1/mode-b/{vehicle}/access-requests",
            AccessRequestResponse.From(request, null));
    }

    private static async Task<Ok<CursorPage<AccessRequestResponse>>> ListRequestsAsync(
        string vehicleId,
        HttpContext context,
        ModeBAccessService access,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(access);

        var vehicle = RequestIds.Require(vehicleId, "vehicleId");
        var page = PageRequest.FromQuery(context.Request);

        var rows = await access.ListRequestsAsync(
            context.User.RequireSubjectId(),
            vehicle,
            ModeBCursors.DecodeRequest(page.Cursor),
            page.OverfetchLimit,
            cancellationToken);

        var result = CursorPage<PendingRequest>.FromOverfetch(rows, page.Limit, ModeBCursors.EncodeRequest);

        return TypedResults.Ok(result.Select(item => AccessRequestResponse.From(item.Row, item.Contact)));
    }

    private static async Task<Ok<AcceptModeBAccessResponse>> AcceptAsync(
        string requestId,
        HttpContext context,
        ModeBAccessService access,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(access);

        var accepted = await access.AcceptAsync(
            context.User.RequireSubjectId(), RequestIds.Require(requestId, "requestId"), cancellationToken);

        return TypedResults.Ok(new AcceptModeBAccessResponse(
            accepted.Request.RequestId, accepted.Grant.GrantId, accepted.Subscription.SubscriptionId));
    }

    private static async Task<Ok<AccessRequestResponse>> RejectAsync(
        string requestId,
        RejectModeBAccessBody? body,
        HttpContext context,
        ModeBAccessService access,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(access);

        var rejected = await access.RejectAsync(
            context.User.RequireSubjectId(), RequestIds.Require(requestId, "requestId"), cancellationToken);

        return TypedResults.Ok(AccessRequestResponse.From(rejected, null));
    }

    // -----------------------------------------------------------------------------------------
    // Subscriptions
    // -----------------------------------------------------------------------------------------

    /// <remarks>
    /// The <c>{passengerId}</c> in the path is the caller's own and nobody else's. A back-office role
    /// is <b>not</b> admitted here, unlike <c>GET /v1/fees/{driverId}/history</c>: those are the
    /// platform's charges against a driver and Finance answers disputes about them, while these are a
    /// passenger's private arrangements with a fleet and the Admin Portal has no screen for them.
    /// </remarks>
    private static async Task<Ok<CursorPage<ModeBSubscriptionResponse>>> ListSubscriptionsAsync(
        string passengerId,
        HttpContext context,
        ModeBAccessService access,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(access);

        var passenger = RequireSelf(context, passengerId);
        var page = PageRequest.FromQuery(context.Request);

        var rows = await access.ListSubscriptionsAsync(
            passenger, ModeBCursors.DecodeSubscription(page.Cursor), page.OverfetchLimit, cancellationToken);

        var result = CursorPage<SubscriptionRow>.FromOverfetch(rows, page.Limit, ModeBCursors.EncodeSubscription);

        return TypedResults.Ok(result.Select(ModeBSubscriptionResponse.From));
    }

    private static async Task<Ok<ModeBSubscriptionResponse>> UnsubscribeAsync(
        string subscriptionId,
        HttpContext context,
        ModeBAccessService access,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(access);

        var cancelled = await access.UnsubscribeAsync(
            context.User.RequireSubjectId(),
            RequestIds.Require(subscriptionId, "subscriptionId"),
            cancellationToken);

        return TypedResults.Ok(ModeBSubscriptionResponse.From(cancelled));
    }

    // -----------------------------------------------------------------------------------------
    // The roster
    // -----------------------------------------------------------------------------------------

    private static async Task<Ok<CursorPage<SubscriberRowResponse>>> ListRosterAsync(
        string vehicleId,
        HttpContext context,
        ModeBAccessService access,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(access);

        var page = PageRequest.FromQuery(context.Request);

        var rows = await access.ListRosterAsync(
            context.User.RequireSubjectId(),
            RequestIds.Require(vehicleId, "vehicleId"),
            ModeBCursors.DecodeRoster(page.Cursor),
            page.OverfetchLimit,
            cancellationToken);

        var result = CursorPage<RosterEntry>.FromOverfetch(rows, page.Limit, ModeBCursors.EncodeRoster);

        return TypedResults.Ok(result.Select(SubscriberRowResponse.From));
    }

    private static async Task<NoContent> DeleteSubscriberAsync(
        string vehicleId,
        string subscriberId,
        HttpContext context,
        ModeBAccessService access,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(access);

        await access.DeleteSubscriberAsync(
            context.User.RequireSubjectId(),
            RequestIds.Require(vehicleId, "vehicleId"),
            RequestIds.Require(subscriberId, "subscriberId"),
            cancellationToken);

        return TypedResults.NoContent();
    }

    private static async Task<Ok<SubscriberRowResponse>> SetFareAsync(
        string vehicleId,
        string subscriberId,
        SetSubscriberFareBody? body,
        HttpContext context,
        ModeBAccessService access,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(access);

        if (body?.MonthlyFareMinor is not { } fare)
        {
            throw new MageRideException(MageRideErrors.InvalidAmount, "monthlyFareMinor is required.");
        }

        var entry = await access.SetFareAsync(
            context.User.RequireSubjectId(),
            RequestIds.Require(vehicleId, "vehicleId"),
            RequestIds.Require(subscriberId, "subscriberId"),
            fare,
            cancellationToken);

        return TypedResults.Ok(SubscriberRowResponse.From(entry));
    }

    // -----------------------------------------------------------------------------------------
    // Payments
    // -----------------------------------------------------------------------------------------

    private static async Task<Ok<SubscriptionPaymentResponse>> PayAsync(
        string subscriptionId,
        PayModeBSubscriptionBody? body,
        HttpContext context,
        ModeBPaymentService payments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payments);

        var method = body?.Method?.Trim();

        if (string.IsNullOrEmpty(method) || !SubscriptionPayMethods.All.Contains(method))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["method"] = [$"method must be one of {string.Join(", ", SubscriptionPayMethods.All)}."],
            });
        }

        var initiated = await payments.PayAsync(
            context.User.RequireSubjectId(),
            RequestIds.Require(subscriptionId, "subscriptionId"),
            method,
            BusinessDates.Optional(body?.PeriodMonth, "periodMonth") is { } month
                ? SubscriptionCycles.PeriodOf(month)
                : null,
            cancellationToken);

        return TypedResults.Ok(SubscriptionPaymentResponse.From(initiated.Payment, initiated.PayTo));
    }

    /// <remarks>
    /// Multipart, one <c>file</c> part, read as a stream rather than buffered — a bank-app screenshot
    /// is megabytes and every one of them would otherwise sit in this process's heap.
    /// </remarks>
    private static async Task<Ok<SubscriptionPaymentResponse>> UploadSlipAsync(
        string paymentId,
        HttpContext context,
        ModeBPaymentService payments,
        IOptions<SubscriptionOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payments);
        ArgumentNullException.ThrowIfNull(options);

        if (!context.Request.HasFormContentType)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["file"] = ["Expected multipart/form-data with a `file` part."],
            });
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        var file = form.Files["file"]
                   ?? throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
                   {
                       ["file"] = ["No `file` part in the upload."],
                   });

        TransferSlipUpload.RequireWithinLimit(file.Length, options.Value.SlipMaxBytes);

        await using var content = file.OpenReadStream();

        var payment = await payments.AttachSlipAsync(
            context.User.RequireSubjectId(),
            RequestIds.Require(paymentId, "paymentId"),
            file.FileName,
            content,
            cancellationToken);

        return TypedResults.Ok(
            SubscriptionPaymentResponse.From(payment, slipUrl: payments.SlipLinkFor(payment)));
    }

    private static async Task<Ok<SubscriptionPaymentResponse>> ConfirmSlipAsync(
        string paymentId,
        HttpContext context,
        ModeBPaymentService payments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payments);

        var payment = await payments.ConfirmAsync(
            context.User.RequireSubjectId(), RequestIds.Require(paymentId, "paymentId"), cancellationToken);

        return TypedResults.Ok(
            SubscriptionPaymentResponse.From(payment, slipUrl: payments.SlipLinkFor(payment)));
    }

    private static async Task<Ok<SubscriptionPaymentResponse>> MarkCashAsync(
        string vehicleId,
        string subscriberId,
        MarkCashBody? body,
        HttpContext context,
        ModeBPaymentService payments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payments);

        if (body?.AmountMinor is not { } amount)
        {
            throw new MageRideException(MageRideErrors.InvalidAmount, "amountMinor is required.");
        }

        var payment = await payments.MarkCashAsync(
            context.User.RequireSubjectId(),
            RequestIds.Require(vehicleId, "vehicleId"),
            RequestIds.Require(subscriberId, "subscriberId"),
            amount,
            BusinessDates.Optional(body.PeriodMonth, "periodMonth") is { } month
                ? SubscriptionCycles.PeriodOf(month)
                : null,
            cancellationToken);

        return TypedResults.Ok(SubscriptionPaymentResponse.From(payment));
    }

    private static async Task<Ok<CursorPage<SubscriptionPaymentResponse>>> ListMyPaymentsAsync(
        string subscriptionId,
        HttpContext context,
        ModeBPaymentService payments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payments);

        var page = PageRequest.FromQuery(context.Request);

        var rows = await payments.ListForPassengerAsync(
            context.User.RequireSubjectId(),
            RequestIds.Require(subscriptionId, "subscriptionId"),
            ModeBCursors.DecodePayment(page.Cursor),
            page.OverfetchLimit,
            cancellationToken);

        return TypedResults.Ok(PageOf(rows, page, payments));
    }

    private static async Task<Ok<CursorPage<SubscriptionPaymentResponse>>> ListSubscriberPaymentsAsync(
        string vehicleId,
        string subscriberId,
        HttpContext context,
        ModeBPaymentService payments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payments);

        var page = PageRequest.FromQuery(context.Request);

        var rows = await payments.ListForSubscriberAsync(
            context.User.RequireSubjectId(),
            RequestIds.Require(vehicleId, "vehicleId"),
            RequestIds.Require(subscriberId, "subscriberId"),
            ModeBCursors.DecodePayment(page.Cursor),
            page.OverfetchLimit,
            cancellationToken);

        return TypedResults.Ok(PageOf(rows, page, payments));
    }

    // -----------------------------------------------------------------------------------------
    // Provider callbacks
    // -----------------------------------------------------------------------------------------

    private static Task<Ok<CallbackAcceptedResponse>> OnepayWebhookAsync(
        HttpContext context,
        ModeBPaymentService payments,
        IOptions<SubscriptionOptions> options,
        ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        HandleCallbackAsync(
            SubscriptionPayMethods.Onepay,
            options?.Value.OnepayWebhookSecret,
            context,
            payments,
            loggers,
            cancellationToken);

    private static Task<Ok<CallbackAcceptedResponse>> LankaQrConfirmAsync(
        HttpContext context,
        ModeBPaymentService payments,
        IOptions<SubscriptionOptions> options,
        ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        HandleCallbackAsync(
            SubscriptionPayMethods.LankaQrScan,
            options?.Value.LankaQrWebhookSecret,
            context,
            payments,
            loggers,
            cancellationToken);

    /// <summary>Verifies the signature over the <b>raw</b> body, then settles.</summary>
    /// <remarks>
    /// <para>
    /// <b>The raw bytes, before any parsing</b> (<c>_shared.yaml</c>: "verified before any body
    /// parsing"). Deserialising and re-serialising changes whitespace and key order, so the digest of
    /// a round-tripped body is not the digest the provider signed.
    /// </para>
    /// <para>
    /// <b>Unsigned is refused, and an unset secret refuses everything.</b> A callback that marks a
    /// month paid without a signature is a free-subscription endpoint for anyone who finds the URL —
    /// and the money it would falsely settle is the fleet owner's.
    /// </para>
    /// </remarks>
    private static async Task<Ok<CallbackAcceptedResponse>> HandleCallbackAsync(
        string method,
        string? secret,
        HttpContext context,
        ModeBPaymentService payments,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payments);
        ArgumentNullException.ThrowIfNull(loggers);

        var logger = loggers.CreateLogger(typeof(ModeBEndpoints));

        context.Request.EnableBuffering();

        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, cancellationToken);
        var raw = buffer.ToArray();
        context.Request.Body.Position = 0;

        var presented = context.Request.Headers[WebhookSignature.HeaderName].ToString();

        if (!WebhookSignature.IsValid(raw, presented, secret))
        {
            logger.LogWarning(
                "A {Method} Mode B subscription callback arrived with an invalid or missing {Header} and was "
                + "refused. Secret configured: {HasSecret}.",
                method,
                WebhookSignature.HeaderName,
                !string.IsNullOrWhiteSpace(secret));

            throw new MageRideException(
                MageRideErrors.Unauthorized, "The callback signature could not be verified.");
        }

        var body = JsonSerializer.Deserialize<SubscriptionProviderCallbackBody>(raw, MageRideJson.Options)
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

        await payments.SettleFromCallbackAsync(
            new ProviderCallback(
                body.ProviderTransactionId,
                RequestIds.Optional(body.PaymentId),
                body.Status ?? string.Empty,
                body.AmountMinor),
            cancellationToken);

        return TypedResults.Ok(new CallbackAcceptedResponse(true));
    }

    // -----------------------------------------------------------------------------------------
    // Signed document links
    // -----------------------------------------------------------------------------------------

    private static async Task<IResult> GetFileAsync(
        string kind,
        string id,
        string? expires,
        string? signature,
        HttpContext context,
        ModeBPaymentService payments,
        IModeBFileLinks links,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payments);
        ArgumentNullException.ThrowIfNull(links);

        var documentId = RequestIds.Require(id, "id");

        // A bad signature and an expired one are both 403 with the same message: distinguishing them
        // tells somebody probing which half of a forged link to work on.
        if (!links.Verify(kind, documentId, expires, signature))
        {
            throw new MageRideException(
                MageRideErrors.Forbidden, "This link is not one this service issued, or it has expired.");
        }

        var opened = await payments.OpenFileAsync(kind, documentId, context.User.SubjectId(), cancellationToken);

        // Δ D-36: on a bucket the bytes are not this process's to stream, so the caller follows a
        // short-lived presigned URL instead. The signature was verified above, so the redirect is
        // only ever issued to somebody who proved the link.
        if (opened is not { } file)
        {
            throw new MageRideException(MageRideErrors.NotFound, "This document is no longer stored.");
        }

        return file.RedirectUrl is { } direct
            ? TypedResults.Redirect(direct, permanent: false, preserveMethod: false)
            : TypedResults.Stream(file.Content!, file.ContentType!);
    }

    // -----------------------------------------------------------------------------------------

    private static CursorPage<SubscriptionPaymentResponse> PageOf(
        IReadOnlyList<PaymentRow> rows, PageRequest page, ModeBPaymentService payments)
    {
        var result = CursorPage<PaymentRow>.FromOverfetch(rows, page.Limit, ModeBCursors.EncodePayment);

        return result.Select(row => SubscriptionPaymentResponse.From(row, slipUrl: payments.SlipLinkFor(row)));
    }

    /// <summary>
    /// The <c>{passengerId}</c>-in-the-path rule for this surface: the caller themselves, nobody else.
    /// </summary>
    /// <remarks>
    /// A malformed id is <c>403</c> rather than <c>400</c>: whatever it was, it was not the caller's,
    /// and answering "that is not a ULID" for somebody else's identifier is a shape oracle.
    /// </remarks>
    private static Guid RequireSelf(HttpContext context, string requested)
    {
        var subject = context.User.RequireSubjectId();

        return Ulids.TryParse(requested, out var parsed) && parsed == subject
            ? subject
            : throw new MageRideException(MageRideErrors.Forbidden, "These subscriptions are not yours.");
    }
}

/// <summary>
/// The opaque positions the four Mode B lists page on.
/// </summary>
/// <remarks>
/// Every one is an <c>(instant, id)</c> pair rather than the instant alone. Rows on these lists share
/// timestamps routinely — a fleet accepting a school's worth of requests in one sitting, a passenger
/// whose failed attempt and successful transfer land in the same second — and a timestamp-only cursor
/// silently drops whichever row straddles a page boundary. Unsigned: the value carries only an
/// ordering position, and every query is scoped by the caller's own identity regardless of what the
/// cursor says.
/// </remarks>
internal static class ModeBCursors
{
    private sealed record Position(DateTimeOffset At, Guid Id);

    public static string EncodeRequest(PendingRequest item) =>
        Encode(item.Row.CreatedAt, item.Row.RequestId);

    public static (DateTimeOffset RequestedAt, Guid RequestId)? DecodeRequest(string? cursor) =>
        Decode(cursor);

    public static string EncodeSubscription(SubscriptionRow row) => Encode(row.CreatedAt, row.SubscriptionId);

    public static (DateTimeOffset CreatedAt, Guid SubscriptionId)? DecodeSubscription(string? cursor) =>
        Decode(cursor);

    public static string EncodeRoster(RosterEntry entry) => Encode(entry.Row.GrantedAt, entry.Row.SubscriberId);

    public static (DateTimeOffset GrantedAt, Guid SubscriberId)? DecodeRoster(string? cursor) => Decode(cursor);

    public static string EncodePayment(PaymentRow row) => Encode(row.CreatedAt, row.PaymentId);

    public static (DateTimeOffset CreatedAt, Guid PaymentId)? DecodePayment(string? cursor) => Decode(cursor);

    private static string Encode(DateTimeOffset at, Guid id) =>
        CursorCodec.Unsigned.Encode(new Position(at, id));

    private static (DateTimeOffset At, Guid Id)? Decode(string? cursor) =>
        CursorCodec.Unsigned.TryDecode<Position>(cursor, out var position) && position is not null
            ? (position.At, position.Id)
            : null;
}
