using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using MageRide.Shared.Time;
using MageRide.Subscriptions.Domain;
using MageRide.Subscriptions.Persistence;
using Npgsql;

namespace MageRide.Subscriptions.ModeB;

/// <summary>
/// Where the passenger sends the money — composed live from the org's verified payout profile and
/// never denormalised onto the payment (AL-49, §18b).
/// </summary>
public sealed record PayToDetails(
    string? LankaqrImageUrl, string? Bank, string? Branch, string? AccountNo, string? AccountHolderName);

/// <summary>A payment and, when the method needs one, the owner's pay-to block.</summary>
public sealed record InitiatedPayment(PaymentRow Payment, PayToDetails? PayTo);

/// <summary>What a provider callback said (R-19).</summary>
public sealed record ProviderCallback(
    string ProviderTransactionId, Guid? PaymentId, string Status, long? AmountMinor);

/// <summary>
/// Epic 23's money half: the subscriber's monthly fare on its way to the fleet owner (BR-23.10).
/// </summary>
/// <remarks>
/// <para>
/// <b>MageRide holds none of this money and takes no cut.</b> There is no call to wallet-svc in this
/// file, no journal entry, no account and no commission — §18b forbids
/// <c>subscription.payments</c> ever posting to <c>billing.journal_entries</c>, and migration 1202
/// gives the table no column that could hold a posting id. The platform's own Mode B fee
/// (<c>billing.monthly_subscriptions</c>, C047) is the ledgered one and lives in a different file for
/// exactly that reason.
/// </para>
/// <para>
/// <b>What this service actually does with a payment is record it.</b> Two of the four methods have
/// no machine confirmation at all — an online transfer is confirmed by the owner looking at a
/// screenshot, and cash by the owner saying it arrived — so the row is a statement of what the two
/// parties agree happened, and the due date moves when it becomes <c>paid</c>. That is the whole
/// mechanism.
/// </para>
/// </remarks>
internal sealed class ModeBPaymentService(
    INpgsqlConnectionFactory connections,
    IUnitOfWorkFactory unitOfWorkFactory,
    IModeBRegistryRepository registry,
    IModeBAccessRepository access,
    ISubscriptionPaymentRepository payments,
    IModeBFileLinks links,
    ITransferSlipStore slips,
    TimeProvider clock,
    ILogger<ModeBPaymentService> logger)
{
    /// <summary>The methods a passenger may start themselves (item 16c/16e).</summary>
    private static readonly IReadOnlySet<string> PassengerMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        SubscriptionPayMethods.LankaQrDeeplink,
        SubscriptionPayMethods.LankaQrScan,
        SubscriptionPayMethods.Onepay,
        SubscriptionPayMethods.OnlineTransfer,
    };

    /// <summary>US-23.3 — open the pay sheet for a month.</summary>
    public async Task<InitiatedPayment> PayAsync(
        Guid passengerId,
        Guid subscriptionId,
        string method,
        DateOnly? periodMonth,
        CancellationToken cancellationToken)
    {
        // Cash is not a passenger's to declare: US-23.6 hands it to a collector and only the owner
        // may say it arrived. A passenger who could record one would mark their own month paid.
        if (!PassengerMethods.Contains(method))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["method"] =
                [
                    "method must be one of lankaqr_deeplink, lankaqr_scan, onepay or online_transfer. "
                    + "Cash is recorded by the fleet owner (US-23.6).",
                ],
            });
        }

        var now = clock.GetUtcNow();

        SubscriptionRow subscription;
        ModeBVehicle vehicle;

        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            subscription = await access.FindSubscriptionAsync(connection, null, subscriptionId, cancellationToken)
                           ?? throw NoSuchSubscription(subscriptionId);

            if (subscription.PassengerId != passengerId)
            {
                throw new MageRideException(
                    MageRideErrors.Forbidden, "This subscription belongs to another passenger.");
            }

            vehicle = await registry.ReadVehicleAsync(
                          connection, null, subscription.VehicleId, passengerId, cancellationToken)
                      ?? throw new MageRideException(
                          MageRideErrors.VehicleNotFound, $"No vehicle {subscription.VehicleId}.");
        }

        RequireCollectable(subscription);

        var payTo = await RequirePayToAsync(vehicle, method, cancellationToken);
        var period = periodMonth ?? DuePeriodOf(subscription, now);
        var amountMinor = subscription.MonthlyFareMinor!.Value;

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var live = await payments.FindLiveForPeriodAsync(
            unitOfWork.Connection, unitOfWork.Transaction, subscriptionId, period, cancellationToken);

        var payment = live switch
        {
            { Status: SubscriptionPaymentStatuses.Paid } => throw new MageRideException(
                MageRideErrors.Conflict,
                $"{SubscriptionCycles.Format(period)} is already paid for."),

            // The slip is with the owner. Re-issuing the sheet would clear it and lose the evidence
            // the confirm is waiting on.
            { Status: SubscriptionPaymentStatuses.PendingVerification } => throw new MageRideException(
                MageRideErrors.Conflict,
                $"A transfer slip for {SubscriptionCycles.Format(period)} is already waiting for the owner "
                + "to confirm it."),

            not null => await payments.ChangeMethodAsync(
                            unitOfWork, live.PaymentId, method, amountMinor, cancellationToken)
                        ?? throw new MageRideException(
                            MageRideErrors.Conflict,
                            $"The payment for {SubscriptionCycles.Format(period)} moved while it was being "
                            + "re-opened."),

            null => await InsertOrConflictAsync(
                unitOfWork,
                subscription,
                period,
                method,
                amountMinor,
                SubscriptionPaymentStatuses.Initiated,
                confirmedBy: null,
                now,
                cancellationToken),
        };

        await unitOfWork.CommitAsync(cancellationToken);

        return new InitiatedPayment(payment, payTo);
    }

    /// <summary>US-23.4 — the transfer screenshot arrives.</summary>
    public async Task<PaymentRow> AttachSlipAsync(
        Guid passengerId,
        Guid paymentId,
        string? fileName,
        Stream content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        PaymentRow payment;

        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            payment = await payments.FindAsync(connection, null, paymentId, cancellationToken)
                      ?? throw NoSuchPayment(paymentId);
        }

        if (payment.PassengerId != passengerId)
        {
            throw new MageRideException(MageRideErrors.Forbidden, "This payment belongs to another passenger.");
        }

        if (!string.Equals(payment.Method, SubscriptionPayMethods.OnlineTransfer, StringComparison.Ordinal))
        {
            throw new MageRideException(
                MageRideErrors.Conflict,
                $"Payment {paymentId} is a {payment.Method} payment. A transfer slip belongs to an "
                + "online_transfer (US-23.4).");
        }

        // The file is written before the transaction, the same order ride-svc's proof photo takes: a
        // payment that moved in between leaves an orphan file rather than a pending_verification row
        // with no evidence behind it.
        var stored = await slips.SaveAsync(paymentId, fileName, content, cancellationToken);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var updated = await payments.AttachSlipAsync(unitOfWork, paymentId, stored.StorageUrl, cancellationToken)
                      ?? throw new MageRideException(
                          MageRideErrors.Conflict,
                          $"Payment {paymentId} is {payment.Status} and no longer takes a slip.");

        await unitOfWork.CommitAsync(cancellationToken);

        return updated;
    }

    /// <summary>Item 16f — the owner confirms a transfer slip.</summary>
    public async Task<PaymentRow> ConfirmAsync(Guid callerId, Guid paymentId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var payment = await payments.FindAsync(
                          unitOfWork.Connection, unitOfWork.Transaction, paymentId, cancellationToken)
                      ?? throw NoSuchPayment(paymentId);

        var vehicle = await registry.ReadVehicleAsync(
                          unitOfWork.Connection, unitOfWork.Transaction, payment.VehicleId, callerId, cancellationToken)
                      ?? throw new MageRideException(
                          MageRideErrors.VehicleNotFound, $"No vehicle {payment.VehicleId}.");

        ModeBAccessService.RequireOwn(vehicle);

        var confirmed = await payments.MarkPaidAsync(
                            unitOfWork,
                            paymentId,
                            payment.Method,
                            amountMinor: null,
                            confirmedBy: callerId,
                            gatewayRef: null,
                            requiredStatus: SubscriptionPaymentStatuses.PendingVerification,
                            now,
                            cancellationToken)
                        ?? throw new MageRideException(
                            MageRideErrors.Conflict,
                            $"Payment {paymentId} is {payment.Status}, not awaiting confirmation. Only a slip "
                            + "the passenger has uploaded can be confirmed (US-23.4).");

        await AdvanceDueAsync(unitOfWork, confirmed, now, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Owner {CallerId} confirmed transfer {PaymentId} for vehicle {VehicleId}, period {Period}",
            callerId,
            paymentId,
            payment.VehicleId,
            payment.PeriodMonth);

        return confirmed;
    }

    /// <summary>US-23.6 — the owner marks cash received.</summary>
    public async Task<PaymentRow> MarkCashAsync(
        Guid callerId,
        Guid vehicleId,
        Guid subscriberId,
        long amountMinor,
        DateOnly? periodMonth,
        CancellationToken cancellationToken)
    {
        if (amountMinor < 0)
        {
            throw new MageRideException(MageRideErrors.InvalidAmount, "amountMinor must not be negative.");
        }

        var now = clock.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var vehicle = await registry.ReadVehicleAsync(
                          unitOfWork.Connection, unitOfWork.Transaction, vehicleId, callerId, cancellationToken)
                      ?? throw new MageRideException(MageRideErrors.VehicleNotFound, $"No vehicle {vehicleId}.");

        ModeBAccessService.RequireOwn(vehicle);

        _ = await access.FindGrantAsync(
                unitOfWork.Connection, unitOfWork.Transaction, vehicleId, subscriberId, cancellationToken)
            ?? throw new MageRideException(MageRideErrors.NotFound, $"No subscriber {subscriberId} on this vehicle.");

        var subscription = await access.FindLiveSubscriptionForGrantAsync(
                               unitOfWork.Connection, unitOfWork.Transaction, subscriberId, cancellationToken)
                           ?? throw new MageRideException(
                               MageRideErrors.Conflict,
                               "This subscriber has no live subscription to receive a payment against.");

        RequireCollectable(subscription);

        var period = periodMonth ?? DuePeriodOf(subscription, now);

        var live = await payments.FindLiveForPeriodAsync(
            unitOfWork.Connection, unitOfWork.Transaction, subscription.SubscriptionId, period, cancellationToken);

        if (live is { Status: SubscriptionPaymentStatuses.Paid })
        {
            throw new MageRideException(
                MageRideErrors.Conflict, $"{SubscriptionCycles.Format(period)} is already paid for.");
        }

        // A month the passenger had already opened a sheet for is completed as cash rather than left
        // beside a second row: ux_subpay_period admits one live payment per month, and the passenger
        // handing over cash after tapping Pay is the ordinary case rather than the odd one.
        var paid = live is null
            ? await InsertOrConflictAsync(
                unitOfWork,
                subscription,
                period,
                SubscriptionPayMethods.Cash,
                amountMinor,
                SubscriptionPaymentStatuses.Paid,
                callerId,
                now,
                cancellationToken)
            : await payments.MarkPaidAsync(
                  unitOfWork,
                  live.PaymentId,
                  SubscriptionPayMethods.Cash,
                  amountMinor,
                  callerId,
                  gatewayRef: null,
                  requiredStatus: null,
                  now,
                  cancellationToken)
              ?? throw new MageRideException(
                  MageRideErrors.Conflict,
                  $"The payment for {SubscriptionCycles.Format(period)} was settled by another path.");

        await AdvanceDueAsync(unitOfWork, paid, now, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Owner {CallerId} marked cash received for subscriber {SubscriberId} on vehicle {VehicleId}, "
            + "period {Period}",
            callerId,
            subscriberId,
            vehicleId,
            period);

        return paid;
    }

    /// <summary>
    /// The OnePay and LankaQR confirmations, which are the same handler with two secrets.
    /// </summary>
    /// <remarks>
    /// <b>Idempotent on <c>provider_transaction_id</c> (R-19).</b> A redelivery finds the row already
    /// <c>paid</c>, the guarded UPDATE returns nothing, and the due date is <em>not</em> advanced a
    /// second time — which is the half that matters, because a double advance would give the
    /// subscriber a free month. The callback is answered <c>200</c> either way, because that is what
    /// stops a provider retrying for ever.
    /// </remarks>
    public async Task SettleFromCallbackAsync(ProviderCallback callback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var now = clock.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var payment = callback.PaymentId is { } id
            ? await payments.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, id, cancellationToken)
            : await payments.FindByGatewayRefAsync(
                unitOfWork.Connection, unitOfWork.Transaction, callback.ProviderTransactionId, cancellationToken);

        if (payment is null)
        {
            throw new MageRideException(
                MageRideErrors.NotFound,
                "This callback names no subscription payment this platform started.");
        }

        switch (callback.Status.ToUpperInvariant())
        {
            case "SUCCESS":
                var settled = await payments.MarkPaidAsync(
                    unitOfWork,
                    payment.PaymentId,
                    payment.Method,
                    callback.AmountMinor,
                    confirmedBy: null,
                    callback.ProviderTransactionId,
                    requiredStatus: null,
                    now,
                    cancellationToken);

                if (settled is null)
                {
                    logger.LogInformation(
                        "A {Method} callback for payment {PaymentId} was a redelivery; the month is already paid "
                        + "and the due date was not advanced again.",
                        payment.Method,
                        payment.PaymentId);
                    break;
                }

                await AdvanceDueAsync(unitOfWork, settled, now, cancellationToken);
                break;

            case "FAILED":
                // The month is freed for another attempt: ux_subpay_period is partial over the three
                // live statuses, so a failed row does not block the retry.
                await payments.MarkFailedAsync(
                    unitOfWork, payment.PaymentId, callback.ProviderTransactionId, cancellationToken);
                break;

            default:
                // PENDING. The gateway will call again; changing nothing is the whole handling.
                break;
        }

        await unitOfWork.CommitAsync(cancellationToken);
    }

    /// <summary>SCR-PA-025b — the passenger's history for one subscription.</summary>
    public async Task<IReadOnlyList<PaymentRow>> ListForPassengerAsync(
        Guid passengerId,
        Guid subscriptionId,
        (DateTimeOffset CreatedAt, Guid PaymentId)? after,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var subscription = await access.FindSubscriptionAsync(connection, null, subscriptionId, cancellationToken)
                           ?? throw NoSuchSubscription(subscriptionId);

        if (subscription.PassengerId != passengerId)
        {
            throw new MageRideException(MageRideErrors.Forbidden, "This subscription belongs to another passenger.");
        }

        return await payments.ListForSubscriptionAsync(subscriptionId, after, limit, cancellationToken);
    }

    /// <summary>SCR-FP-012 — the owner's per-subscriber ledger.</summary>
    public async Task<IReadOnlyList<PaymentRow>> ListForSubscriberAsync(
        Guid callerId,
        Guid vehicleId,
        Guid subscriberId,
        (DateTimeOffset CreatedAt, Guid PaymentId)? after,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var vehicle = await registry.ReadVehicleAsync(connection, null, vehicleId, callerId, cancellationToken)
                      ?? throw new MageRideException(MageRideErrors.VehicleNotFound, $"No vehicle {vehicleId}.");

        ModeBAccessService.RequireOwn(vehicle);

        var grant = await access.FindGrantAsync(connection, null, vehicleId, subscriberId, cancellationToken)
                    ?? throw new MageRideException(
                        MageRideErrors.NotFound, $"No subscriber {subscriberId} on this vehicle.");

        // By (vehicle, passenger), not by subscription: a subscriber who left and rejoined has two
        // subscription rows and one ledger, and the owner's screen is about the person.
        return await payments.ListForSubscriberAsync(
            vehicleId, grant.PassengerId, after, limit, cancellationToken);
    }

    /// <summary>The signed URL for a payment's slip, when it has one.</summary>
    public string? SlipLinkFor(PaymentRow payment)
    {
        ArgumentNullException.ThrowIfNull(payment);

        return string.IsNullOrWhiteSpace(payment.SlipUrl)
            ? null
            : links.Create(ModeBFileKinds.Slip, payment.PaymentId);
    }

    /// <summary>Resolves and opens a signed document link, or throws the right 403/404.</summary>
    public async Task<(Stream Content, string ContentType)?> OpenFileAsync(
        string kind, Guid id, Guid? callerId, CancellationToken cancellationToken)
    {
        var storageUrl = kind switch
        {
            ModeBFileKinds.Slip => await ReadSlipUrlAsync(id, callerId, cancellationToken),
            ModeBFileKinds.LankaQr => await registry.ReadPayoutQrUrlAsync(id, cancellationToken),
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(storageUrl))
        {
            throw new MageRideException(MageRideErrors.NotFound, "No such document.");
        }

        return slips.Open(storageUrl);
    }

    /// <summary>The month a payment covers when the caller does not name one.</summary>
    private DateOnly DuePeriodOf(SubscriptionRow subscription, DateTimeOffset now) =>
        SubscriptionCycles.PeriodOf(subscription.NextDue ?? BusinessCalendar.BusinessDate(now));

    /// <summary>
    /// A subscription that can be collected against: live, and Paid.
    /// </summary>
    /// <remarks>
    /// A Free one is not an error the passenger made — BR-23.8 gives a Free vehicle no payment UI at
    /// all — so it is a <c>409</c> naming the classification rather than a <c>400</c> about the body.
    /// </remarks>
    private static void RequireCollectable(SubscriptionRow subscription)
    {
        if (!string.Equals(subscription.Status, SubscriptionStatuses.Active, StringComparison.Ordinal))
        {
            throw new MageRideException(
                MageRideErrors.Conflict, $"This subscription is {subscription.Status} and collects nothing.");
        }

        if (!string.Equals(subscription.Billing, SubscriptionBilling.Paid, StringComparison.Ordinal)
            || subscription.MonthlyFareMinor is null)
        {
            throw new MageRideException(
                MageRideErrors.Conflict,
                "This is a Free service payment — there is no fare to pay (BR-23.8).");
        }
    }

    /// <summary>
    /// The <c>payTo</c> block, and the AL-49 gate in front of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Served only from a <c>verified</c> profile.</b> <c>ux_payout_profile_verified</c> makes that
    /// row singular, and because the table is versioned an owner's later edit lands as a new
    /// <c>pending_verification</c> row without disturbing it — so collection continues against the
    /// last verified snapshot and never against an unverified edit (D5' §802).
    /// </para>
    /// <para>
    /// <b>A vehicle belonging to no fleet gets the same 409.</b> A payout profile is an org's, so an
    /// individually-owned Mode B vehicle has nowhere for the money to go; the answer is the same one
    /// and for the same reason, and it is the more useful answer than "no fleet".
    /// </para>
    /// </remarks>
    private async Task<PayToDetails?> RequirePayToAsync(
        ModeBVehicle vehicle, string method, CancellationToken cancellationToken)
    {
        var profile = vehicle.FleetId is { } fleetId
            ? await registry.ReadVerifiedPayoutProfileAsync(fleetId, cancellationToken)
            : null;

        if (profile is null)
        {
            throw new MageRideException(
                MageRideErrors.PayoutProfileNotVerified,
                "This vehicle's operator has no verified bank and payout profile, so a Paid subscription "
                + "cannot collect (AL-49, BR-31.1). The owner completes SCR-FP-002a and a Verification "
                + "Officer approves it.");
        }

        if (SubscriptionPayMethods.IsLankaQr(method))
        {
            return new PayToDetails(
                profile.LankaqrUploadId is null ? null : links.Create(ModeBFileKinds.LankaQr, profile.ProfileId),
                null,
                null,
                null,
                null);
        }

        if (string.Equals(method, SubscriptionPayMethods.OnlineTransfer, StringComparison.Ordinal))
        {
            return new PayToDetails(
                null, profile.Bank, profile.Branch, profile.AccountNo, profile.AccountHolderName);
        }

        // OnePay. The gate above still applies — a Paid subscription of an unverified org collects
        // nothing by any method — but there is no pay-to block to render: a gateway session would have
        // to be opened against the *owner's* merchant account, and no table in this schema binds one
        // to a fleet (registry.driver_payouts is per driver, for D-11 fare settlement). Raised in the
        // C048 handoff; the callback half of the route is complete and settles a session opened
        // elsewhere.
        logger.LogWarning(
            "A OnePay Mode B subscription payment was initiated for fleet {FleetId}. No per-org OnePay merchant "
            + "binding exists in any schema, so no gateway session is created here and the passenger has no "
            + "redirect to follow — LankaQR and online transfer are the working methods until one does.",
            profile.FleetId);

        return null;
    }

    /// <summary>
    /// Inserts a payment, turning the <c>ux_subpay_period</c> race into a <c>409</c> rather than a
    /// <c>500</c>.
    /// </summary>
    /// <remarks>
    /// Two devices tapping Pay at once both find no live row and both insert; the unique index picks
    /// one. The loser is told the month is already in progress, which is true and actionable — a
    /// refresh shows them the sheet the winner opened.
    /// </remarks>
    private async Task<PaymentRow> InsertOrConflictAsync(
        IUnitOfWork unitOfWork,
        SubscriptionRow subscription,
        DateOnly period,
        string method,
        long amountMinor,
        string status,
        Guid? confirmedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            return await payments.InsertAsync(
                unitOfWork, subscription, period, method, amountMinor, status, confirmedBy, now, cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new MageRideException(
                MageRideErrors.Conflict,
                $"A payment for {SubscriptionCycles.Format(period)} is already in progress.");
        }
    }

    /// <summary>Rolls the due date on, once and only for the settlement that actually happened.</summary>
    private async Task AdvanceDueAsync(
        IUnitOfWork unitOfWork, PaymentRow payment, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var subscription = await access.FindSubscriptionAsync(
            unitOfWork.Connection, unitOfWork.Transaction, payment.SubscriptionId, cancellationToken);

        if (subscription is null)
        {
            return;
        }

        var from = subscription.NextDue
                   ?? SubscriptionCycles.FirstDue(BusinessCalendar.BusinessDate(now), subscription.Cycle);

        var next = SubscriptionCycles.Advance(from, subscription.Cycle, subscription.JoinDay);

        await access.AdvanceNextDueAsync(unitOfWork, subscription.SubscriptionId, next, now, cancellationToken);
    }

    private async Task<string?> ReadSlipUrlAsync(Guid paymentId, Guid? callerId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var payment = await payments.FindAsync(connection, null, paymentId, cancellationToken);

        if (payment is null)
        {
            return null;
        }

        // The signature is the credential, so an authenticated caller is not required — but when one
        // is present and is neither the passenger nor somebody who may work the vehicle, the link was
        // forwarded rather than followed.
        if (callerId is { } caller && payment.PassengerId != caller)
        {
            var vehicle = await registry.ReadVehicleAsync(
                connection, null, payment.VehicleId, caller, cancellationToken);

            if (vehicle is not { CanManage: true })
            {
                throw new MageRideException(MageRideErrors.Forbidden, "This slip belongs to another subscription.");
            }
        }

        return payment.SlipUrl;
    }

    private static MageRideException NoSuchSubscription(Guid subscriptionId) =>
        new(MageRideErrors.NotFound, $"No Mode B subscription {subscriptionId}.");

    private static MageRideException NoSuchPayment(Guid paymentId) =>
        new(MageRideErrors.NotFound, $"No Mode B subscription payment {paymentId}.");
}
