using System.Globalization;
using MageRide.Notification.Configuration;
using MageRide.Notification.Domain;
using MageRide.Notification.Persistence;
using MageRide.Notification.Sending;
using MageRide.Notification.Tokens;
using Microsoft.Extensions.Options;

namespace MageRide.Notification.Messaging;

/// <summary>Deep links the apps register, so a push can open the right screen.</summary>
/// <remarks>
/// <c>mageride://package/{rideId}</c> is D6' I-23.3 verbatim (it names SCR-PA-021). The other two
/// follow the same scheme; no spec prints them, and they are recorded in the C051 handoff so the KMP
/// clients and this service cannot drift.
/// </remarks>
internal static class DeepLinks
{
    public static string Ride(Guid rideId) => $"mageride://ride/{rideId}";

    /// <summary>D6' I-23.3: opens SCR-PA-021.</summary>
    public static string Package(Guid rideId) => $"mageride://package/{rideId}";

    public static string Wallet() => "mageride://wallet";

    public static string Documents() => "mageride://documents";
}

/// <summary>
/// <c>dispatch.events</c> — E-01's offer and the two Destination Filter notices.
/// </summary>
internal sealed class DispatchEventHandler(
    INotificationService notifications, ILogger<DispatchEventHandler> logger) : IEventHandler
{
    public async Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        switch (envelope.EventType)
        {
            case "offer.created":
                await OfferAsync(envelope, cancellationToken);
                break;

            case "directional.expiring":
                await DirectionalAsync(envelope, NotificationCatalogue.DirectionalExpiring, cancellationToken);
                break;

            case "directional.cleared":
                await DirectionalAsync(envelope, NotificationCatalogue.DirectionalCleared, cancellationToken);
                break;

            default:
                // Every consumer on this platform ignores what it does not recognise, by event type.
                logger.LogDebug("Ignoring {EventType} on dispatch.events.", envelope.EventType);
                break;
        }
    }

    private async Task OfferAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.Id("driverId") is not { } driverId || envelope.Id("offerId") is not { } offerId)
        {
            logger.LogWarning("An offer.created carried no driverId/offerId; no push is possible.");
            return;
        }

        var rideId = envelope.Id("rideId");
        var fareMinor = envelope.Number("fareEstimateMinor");
        var distanceM = envelope.Number("distanceToPickupM");

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // The silent data message the driver app draws SCR-DA-013 from. `kind` is what the app
            // switches on; the rest is what the incoming-request overlay needs without a round trip.
            ["kind"] = "ride_offer",
            ["offerId"] = offerId.ToString(),
        };

        if (rideId is { } ride)
        {
            values["rideId"] = ride.ToString();
            values["deeplink"] = DeepLinks.Ride(ride);
        }

        if (envelope.Instant("expiresAt") is { } expiresAt)
        {
            values["expiresAt"] = expiresAt.ToString("O", CultureInfo.InvariantCulture);
        }

        // The two values `ride_offer_sms` interpolates (migration 1904). They are on the push too,
        // and that is deliberate: the fallback SMS is rendered from the *same* payload, so a value
        // the push carried and the SMS could not would be a message with a hole in it.
        if (fareMinor is { } fare)
        {
            values["fare"] = PayloadValues.Rupees(fare);
        }

        if (distanceM is { } metres)
        {
            values["distance"] = ((double)metres / 1000).ToString("0.0", CultureInfo.InvariantCulture);
        }

        var receipt = await notifications.EnqueueAsync(
            new NotificationRequest(
                Type: NotificationCatalogue.RideOffer,
                DedupeKey: NotificationDedupe.For("dispatch", offerId.ToString(), NotificationCatalogue.RideOffer),
                UserId: driverId,
                Values: values),
            cancellationToken);

        if (receipt.Outcome is NotificationOutcome.Undeliverable)
        {
            // A driver with no registered handset is a driver dispatch-svc has offered a ride to and
            // who will never see it. Loud: E-01's fallback needs a *push* to go unacked, and this one
            // never left.
            logger.LogWarning(
                "Offer {OfferId} could not be pushed to driver {DriverId}: {Reason}.",
                offerId, driverId, receipt.Reason);
        }
    }

    private async Task DirectionalAsync(EventEnvelope envelope, string type, CancellationToken cancellationToken)
    {
        if (envelope.Id("driverId") is not { } driverId)
        {
            return;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["kind"] = type,
        };

        if (envelope.Number("minutesRemaining") is { } minutes)
        {
            values["minutes"] = minutes.ToString(CultureInfo.InvariantCulture);
        }

        if (envelope.Number("usesRemaining") is { } uses)
        {
            values["usesRemaining"] = uses.ToString(CultureInfo.InvariantCulture);
        }

        if (envelope.Id("filterId") is { } filterId)
        {
            values["filterId"] = filterId.ToString();
        }

        await notifications.EnqueueAsync(
            new NotificationRequest(
                Type: type,

                // Keyed by the filter and the type, not by the event id: DT-08's reminder and DT-04's
                // clear are one each per filter, and a producer that retried its outbox row must not
                // buzz the driver twice.
                DedupeKey: NotificationDedupe.For(
                    "dispatch", (envelope.Id("filterId") ?? driverId).ToString(), type),
                UserId: driverId,
                Values: values),
            cancellationToken);
    }
}

/// <summary>
/// <c>ride.events</c> — the lifecycle, P-02's round-trip, AL-21's package branch and AL-44's proxy
/// link.
/// </summary>
internal sealed class RideEventHandler(
    INotificationService notifications,
    IRecipientRepository recipients,
    ILocationRequestLookup locationRequests,
    IShareTokenMinter tokens,
    IDeliveryCodeStore deliveryCodes,
    IOptions<NotificationOptions> options,
    TimeProvider clock,
    ILogger<RideEventHandler> logger) : IEventHandler
{
    private readonly NotificationOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        switch (envelope.EventType)
        {
            case "ride.accepted":
                await LifecycleAsync(envelope, NotificationCatalogue.DriverAssigned, cancellationToken);
                await ProxyLinkAsync(envelope, cancellationToken);
                break;

            case "ride.driver_arrived":
                await LifecycleAsync(envelope, NotificationCatalogue.DriverArrived, cancellationToken);
                break;

            case "ride.cancelled":
                await LifecycleAsync(envelope, NotificationCatalogue.RideCancelled, cancellationToken, includeDriver: true);
                break;

            case "ride.settled":
                await SettledAsync(envelope, cancellationToken);
                break;

            case "location.request.issued":
                await LocationRequestAsync(envelope, cancellationToken);
                break;

            case "package.picked_up":
                await PackagePickedUpAsync(envelope, cancellationToken);
                break;

            case "package.delivered":
                await PackageDeliveredAsync(envelope, cancellationToken);
                break;

            default:
                logger.LogDebug("Ignoring {EventType} on ride.events.", envelope.EventType);
                break;
        }
    }

    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The state-change pushes. <b>Both sides of a proxy booking are told</b> — the booker because
    /// they arranged the ride and are paying for it, the rider because they are in the car (P-05,
    /// and ride-svc's payload carries both for exactly this).
    /// </summary>
    private async Task LifecycleAsync(
        EventEnvelope envelope, string type, CancellationToken cancellationToken, bool includeDriver = false)
    {
        var rideId = Guid.TryParse(envelope.Key, out var key) ? key : envelope.Id("rideId");

        if (rideId is not { } ride)
        {
            return;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["kind"] = type,
            ["rideId"] = ride.ToString(),
            ["deeplink"] = DeepLinks.Ride(ride),
        };

        if (envelope.Text("state") is { } state)
        {
            values["state"] = state;
        }

        var audience = new List<Guid>(3);

        if ((envelope.Id("bookerId") ?? envelope.Id("passengerId")) is { } booker)
        {
            audience.Add(booker);
        }

        if (envelope.Id("riderId") is { } rider)
        {
            audience.Add(rider);
        }

        if (includeDriver && envelope.Id("driverId") is { } driver)
        {
            audience.Add(driver);
        }

        foreach (var recipient in audience.Distinct())
        {
            await notifications.EnqueueAsync(
                new NotificationRequest(
                    Type: type,

                    // The recipient is part of the key: one `ride.accepted` produces a push to the
                    // booker and another to the rider, and a key without it would make the second
                    // look like a redelivery of the first.
                    DedupeKey: NotificationDedupe.For("ride", ride.ToString(), type, recipient),
                    UserId: recipient,
                    Values: values),
                cancellationToken);
        }
    }

    /// <summary>US-8.15's receipt (R-05's terminal).</summary>
    private async Task SettledAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var rideId = Guid.TryParse(envelope.Key, out var key) ? key : envelope.Id("rideId");
        var payer = envelope.Id("bookerId") ?? envelope.Id("passengerId");

        if (rideId is not { } ride || payer is not { } who)
        {
            return;
        }

        // Nothing was collected — a cancellation that settled at zero, or a dispute. A "payment
        // received: Rs 0.00" push is worse than silence.
        if (envelope.Number("settledMinor") is not { } settled || settled <= 0)
        {
            return;
        }

        await notifications.EnqueueAsync(
            new NotificationRequest(
                Type: NotificationCatalogue.PaymentConfirmed,
                DedupeKey: NotificationDedupe.For(
                    "ride", ride.ToString(), NotificationCatalogue.PaymentConfirmed, who),
                UserId: who,
                Values: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["kind"] = NotificationCatalogue.PaymentConfirmed,
                    ["rideId"] = ride.ToString(),
                    ["deeplink"] = DeepLinks.Ride(ride),
                    ["amount"] = PayloadValues.Rupees(settled),
                }),
            cancellationToken);
    }

    /// <summary>
    /// P-02/P-13's two branches, which is one event with two outcomes (ride-svc's own note on
    /// <c>location.request.issued</c>).
    /// </summary>
    /// <remarks>
    /// <b><c>Pending</c> is the FCM data message</b> — <c>{kind:'location_request', requestId,
    /// bookerName, ttl:300}</c>, exactly D6' §7.4 — metered by the P-12 buckets against the
    /// <em>booker</em>. <b><c>RiderNotRegistered</c> is AL-45</b>: a <c>pickup_confirm</c> token,
    /// minted here and SMSed, never returned anywhere.
    /// </remarks>
    private async Task LocationRequestAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.Id("requestId") is not { } requestId || envelope.Id("bookerId") is not { } bookerId)
        {
            return;
        }

        var state = envelope.Text("state");
        var expiresAt = envelope.Instant("expiresAt") ?? clock.GetUtcNow() + _options.PickupConfirmTokenTtl;

        var booker = await recipients.FindAsync(bookerId, cancellationToken);

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["kind"] = NotificationCatalogue.LocationRequest,
            ["requestId"] = requestId.ToString(),
            ["ttl"] = ((int)(expiresAt - clock.GetUtcNow()).TotalSeconds).ToString(CultureInfo.InvariantCulture),
        };

        if (envelope.Id("riderId") is { } riderId
            && string.Equals(state, "Pending", StringComparison.Ordinal))
        {
            await notifications.EnqueueAsync(
                new NotificationRequest(
                    Type: NotificationCatalogue.LocationRequest,
                    DedupeKey: NotificationDedupe.For(
                        "ride", requestId.ToString(), NotificationCatalogue.LocationRequest),
                    UserId: riderId,
                    Values: values,

                    // P-12 meters the *booker*, not the rider being asked. A rider pinged by five
                    // different bookers has done nothing; a booker pinging five riders has.
                    RateLimitSubject: bookerId),
                cancellationToken);

            return;
        }

        if (!string.Equals(state, "RiderNotRegistered", StringComparison.Ordinal))
        {
            return;
        }

        if (envelope.Text("riderPhone") is not { Length: > 0 } phone)
        {
            // The one place an unhashed number appears in ride-svc's events, and without it AL-45
            // has nobody to SMS. P-03 stores only a digest, so there is no recovering it here.
            logger.LogWarning(
                "Location request {RequestId} is RiderNotRegistered and carried no number; the AL-45 link cannot be sent.",
                requestId);

            return;
        }

        // The token is bound to `rides.location_requests.id`, the surrogate — 0901's foreign key
        // points at the primary key, and 0606 keeps the public `request_id` handle distinct from it
        // on purpose.
        var rowId = await locationRequests.FindIdAsync(requestId, cancellationToken);

        if (rowId is not { } locationRequestId)
        {
            logger.LogWarning(
                "No rides.location_requests row for request {RequestId}; no pickup_confirm token can be minted.",
                requestId);

            return;
        }

        var link = await tokens.MintForLocationRequestAsync(
            locationRequestId,

            // The token cannot outlive the request it stands in for: AL-45's 300 s is the request's
            // own TTL, and the contract pins it at `const: 300`.
            expiresAt,
            cancellationToken);

        values["link"] = link.Url;

        if (booker is not null && envelope.Text("bookerName") is { } bookerName)
        {
            values["bookerName"] = bookerName;
        }

        await notifications.EnqueueAsync(
            new NotificationRequest(
                Type: NotificationCatalogue.PickupConfirmLink,
                DedupeKey: NotificationDedupe.For(
                    "ride", requestId.ToString(), NotificationCatalogue.PickupConfirmLink),
                Phone: phone,
                Values: values),
            cancellationToken);
    }

    /// <summary>
    /// AL-44 / US-8.22: a driver accepted a ride somebody else booked, so the rider gets a link.
    /// </summary>
    /// <remarks>
    /// <b>Only a registered rider can be reached.</b> <c>ride.accepted</c> carries <c>riderId</c> and
    /// no number, and P-03 stores an unregistered proxy rider's MSISDN as a digest and nowhere else
    /// — so for that rider there is no number anywhere on the platform to SMS. Raised in the C051
    /// handoff: <c>ride.accepted</c> would have to carry the rider's phone the way
    /// <c>location.request.issued</c> does.
    /// </remarks>
    private async Task ProxyLinkAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (!envelope.Flag("isProxy") || envelope.Id("riderId") is not { } riderId)
        {
            return;
        }

        var rideId = Guid.TryParse(envelope.Key, out var key) ? key : envelope.Id("rideId");

        if (rideId is not { } ride || !tokens.IsConfigured)
        {
            return;
        }

        var rider = await recipients.FindAsync(riderId, cancellationToken);

        if (rider?.Phone is not { Length: > 0 } phone)
        {
            logger.LogInformation(
                "Proxy ride {RideId} has no number for rider {RiderId}; the AL-44 proxy_rider link is not sent.",
                ride, riderId);

            return;
        }

        var link = await tokens.MintForTripAsync(
            ShareTokenScopes.ProxyRider, ride, clock.GetUtcNow() + _options.ProxyRiderTokenTtl, cancellationToken);

        await notifications.EnqueueAsync(
            new NotificationRequest(
                Type: NotificationCatalogue.ProxyRideLink,
                DedupeKey: NotificationDedupe.For(
                    "ride", ride.ToString(), NotificationCatalogue.ProxyRideLink, riderId),
                UserId: riderId,
                Phone: phone,
                Values: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["kind"] = NotificationCatalogue.ProxyRideLink,
                    ["rideId"] = ride.ToString(),
                    ["link"] = link.Url,
                }),
            cancellationToken);
    }

    /// <summary>
    /// AL-21's branch: registered → FCM deep link into SCR-PA-021; unregistered → SMS with a
    /// <c>package_recipient</c> share token.
    /// </summary>
    /// <remarks>
    /// <b>The delivery OTP travels only on the registered branch.</b> ADD §11.16 hands the code to
    /// the recipient at pickup, and the app is a place to show it; an SMS is not — D6' I-23.3 has the
    /// web page show it "post token validation", so the token is what carries it and the message
    /// body does not.
    /// <para>
    /// <b>Δ C066: on the unregistered branch the code is left where that page can read it.</b> The
    /// sentence above was true and incomplete — public-bff serves SCR-WT-002 and had nothing to read
    /// the code from, because ride-svc keeps only the digest and this event is the one hop the
    /// plaintext takes. <see cref="IDeliveryCodeStore"/> is where it is left, beside the token that
    /// is the only thing allowed to fetch it.
    /// </para>
    /// </remarks>
    private async Task PackagePickedUpAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var rideId = Guid.TryParse(envelope.Key, out var key) ? key : envelope.Id("rideId");

        if (rideId is not { } ride)
        {
            return;
        }

        var phone = envelope.Text("recipientPhone");
        var recipient = string.IsNullOrWhiteSpace(phone)
            ? null
            : await recipients.FindByPhoneAsync(phone, cancellationToken);

        if (recipient is not null)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["kind"] = NotificationCatalogue.PackagePickedUp,
                ["rideId"] = ride.ToString(),
                ["deeplink"] = DeepLinks.Package(ride),
            };

            if (envelope.Text("deliveryOtp") is { Length: > 0 } otp)
            {
                values["deliveryOtp"] = otp;
            }

            await notifications.EnqueueAsync(
                new NotificationRequest(
                    Type: NotificationCatalogue.PackagePickedUp,
                    DedupeKey: NotificationDedupe.For(
                        "ride", ride.ToString(), NotificationCatalogue.PackagePickedUp, recipient.UserId),
                    UserId: recipient.UserId,
                    Values: values),
                cancellationToken);

            return;
        }

        if (string.IsNullOrWhiteSpace(phone) || !tokens.IsConfigured)
        {
            logger.LogWarning(
                "Package {RideId} was picked up and its recipient can be reached neither by app nor by SMS.", ride);

            return;
        }

        var link = await tokens.MintForTripAsync(
            ShareTokenScopes.PackageRecipient,
            ride,
            clock.GetUtcNow() + _options.PackageRecipientTokenTtl,
            cancellationToken);

        // Δ C066. The code stays out of the SMS — that decision is unchanged and argued above — and
        // is left for the page the link opens. Best-effort: a recipient with a link and no code can
        // still be delivered to by photo proof (P-10); one with no link cannot be delivered to at all.
        if (envelope.Text("deliveryOtp") is { Length: > 0 } webOtp)
        {
            await deliveryCodes.PutAsync(ride, webOtp, cancellationToken);
        }

        await notifications.EnqueueAsync(
            new NotificationRequest(
                Type: NotificationCatalogue.PackageOnTheWay,
                DedupeKey: NotificationDedupe.For(
                    "ride", ride.ToString(), NotificationCatalogue.PackageOnTheWay),
                Phone: phone,
                Values: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["kind"] = NotificationCatalogue.PackageOnTheWay,
                    ["rideId"] = ride.ToString(),
                    ["link"] = link.Url,
                }),
            cancellationToken);
    }

    /// <summary>US-10.13. The sender always; the recipient too when they have an account.</summary>
    private async Task PackageDeliveredAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var rideId = Guid.TryParse(envelope.Key, out var key) ? key : envelope.Id("rideId");

        if (rideId is not { } ride)
        {
            return;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["kind"] = NotificationCatalogue.PackageDelivered,
            ["rideId"] = ride.ToString(),
            ["deeplink"] = DeepLinks.Package(ride),
        };

        var audience = new List<Guid>(2);

        if (envelope.Id("passengerId") is { } sender)
        {
            audience.Add(sender);
        }

        if (envelope.Text("recipientPhone") is { Length: > 0 } phone
            && await recipients.FindByPhoneAsync(phone, cancellationToken) is { UserId: { } recipientId })
        {
            audience.Add(recipientId);
        }

        foreach (var who in audience.Distinct())
        {
            await notifications.EnqueueAsync(
                new NotificationRequest(
                    Type: NotificationCatalogue.PackageDelivered,
                    DedupeKey: NotificationDedupe.For(
                        "ride", ride.ToString(), NotificationCatalogue.PackageDelivered, who),
                    UserId: who,
                    Values: values),
                cancellationToken);
        }
    }
}

/// <summary><c>wallet.events</c> — US-9.9's low balance and D-13's daily fee.</summary>
internal sealed class WalletEventHandler(
    INotificationService notifications, ILogger<WalletEventHandler> logger) : IEventHandler
{
    /// <summary><c>billing.journal_entries.kind</c> for the D-13 charge (migration 1101).</summary>
    private const string DailyFeeKind = "daily_fee";

    public async Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.Id("ownerId") is not { } ownerId)
        {
            return;
        }

        switch (envelope.EventType)
        {
            case "wallet.low_balance":
            {
                // D5' §9.4's two clauses are two messages, and wallet-svc already decided which:
                // `severity` travels on the event so every consumer does not re-derive it from the
                // sign of a number.
                var type = string.Equals(envelope.Text("severity"), "top_up_required", StringComparison.Ordinal)
                    ? NotificationCatalogue.TopUpRequired
                    : NotificationCatalogue.LowBalance;

                await notifications.EnqueueAsync(
                    new NotificationRequest(
                        Type: type,

                        // Keyed by the event, not by the wallet: §14.4 throttles this to "once below
                        // Rs 200" and wallet-svc only publishes on the *crossing*, so one event is
                        // one nudge and a redelivery is none.
                        DedupeKey: NotificationDedupe.For("wallet", envelope.Identity, type, ownerId),
                        UserId: ownerId,
                        Values: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["kind"] = type,
                            ["deeplink"] = DeepLinks.Wallet(),
                            ["balance"] = PayloadValues.Rupees(envelope.Number("balanceMinor") ?? 0),
                        }),
                    cancellationToken);

                break;
            }

            case "wallet.debited"
                when string.Equals(envelope.Text("kind"), DailyFeeKind, StringComparison.Ordinal):
            {
                // `amountMinor` is signed as it was posted — negative for a debit — so the message
                // says "Rs 100.00 has been deducted", not "Rs -100.00".
                var amount = Math.Abs(envelope.Number("amountMinor") ?? 0);

                await notifications.EnqueueAsync(
                    new NotificationRequest(
                        Type: NotificationCatalogue.DailyFee,
                        DedupeKey: NotificationDedupe.For(
                            "wallet", envelope.Identity, NotificationCatalogue.DailyFee, ownerId),
                        UserId: ownerId,
                        Values: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["kind"] = NotificationCatalogue.DailyFee,
                            ["deeplink"] = DeepLinks.Wallet(),
                            ["amount"] = PayloadValues.Rupees(amount),
                        }),
                    cancellationToken);

                break;
            }

            default:
                logger.LogDebug("Ignoring {EventType} on wallet.events.", envelope.EventType);
                break;
        }
    }
}

/// <summary><c>registry.events</c> — US-2.14's registration result and E-03's document warnings.</summary>
internal sealed class RegistryEventHandler(
    INotificationService notifications, ILogger<RegistryEventHandler> logger) : IEventHandler
{
    public async Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        (string? type, Guid? recipient, string? deeplink) = envelope.EventType switch
        {
            "vehicle.approved" =>
                (NotificationCatalogue.RegistrationApproved, envelope.Id("ownerId"), DeepLinks.Documents()),

            "document.review_required" =>
                (NotificationCatalogue.RegistrationReviewRequired, envelope.Id("driverId"), DeepLinks.Documents()),

            "document.expiring" =>
                (NotificationCatalogue.DocumentExpiring, envelope.Id("driverId"), DeepLinks.Documents()),

            "document.expired" =>
                (NotificationCatalogue.DocumentExpired, envelope.Id("driverId"), DeepLinks.Documents()),

            _ => (null, null, null),
        };

        if (type is null || recipient is not { } who)
        {
            logger.LogDebug("Ignoring {EventType} on registry.events.", envelope.EventType);
            return;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["kind"] = type,
            ["deeplink"] = deeplink!,
        };

        if (envelope.Id("vehicleId") is { } vehicleId)
        {
            values["vehicleId"] = vehicleId.ToString();
        }

        // `document_expiring` interpolates {{days}}, and E-03 fires at T−30, T−7 and T−1 — so the
        // number is also part of the dedupe key below, or the second warning would look like a
        // redelivery of the first.
        if (envelope.Number("daysRemaining") is { } days)
        {
            values["days"] = days.ToString(CultureInfo.InvariantCulture);
        }

        var subject = envelope.Id("documentId")?.ToString()
                      ?? envelope.Id("vehicleId")?.ToString()
                      ?? envelope.Key;

        await notifications.EnqueueAsync(
            new NotificationRequest(
                Type: type,
                DedupeKey: NotificationDedupe.For(
                    "registry",
                    values.TryGetValue("days", out var d) ? $"{subject}:{d}" : subject,
                    type,
                    who),
                UserId: who,
                Values: values),
            cancellationToken);
    }
}
