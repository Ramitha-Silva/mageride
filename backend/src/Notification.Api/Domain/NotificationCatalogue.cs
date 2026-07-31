using System.Collections.Frozen;

namespace MageRide.Notification.Domain;

/// <summary>The <c>channel</c> values of <c>comms.notifications</c> (migration 1308).</summary>
public static class NotificationChannels
{
    public const string Push = "push";
    public const string Sms = "sms";
}

/// <summary>The <c>status</c> values of <c>comms.notifications</c>.</summary>
public static class NotificationStatuses
{
    /// <summary>Enqueued, due at <c>next_attempt_at</c>.</summary>
    public const string Pending = "Pending";

    /// <summary>Handed to a transport. Terminal for everything but an offer push (E-01).</summary>
    public const string Sent = "Sent";

    /// <summary>The device confirmed it woke up. Only <c>RIDE_OFFER</c> reaches this.</summary>
    public const string Acked = "Acked";

    /// <summary>Out of attempts, or undeliverable on arrival (no device token, no number).</summary>
    public const string Failed = "Failed";

    /// <summary>The recipient muted this type (US-10.7), or a limit refused it (P-12).</summary>
    public const string Suppressed = "Suppressed";

    /// <summary>E-01: the push went unacked for three seconds and an SMS replaced it.</summary>
    public const string FellBackToSms = "FellBackToSms";
}

/// <summary>Push priority. <c>high</c> is FCM <c>priority=high</c> + APNs <c>apns-priority: 10</c>.</summary>
public static class NotificationPriorities
{
    public const string Normal = "normal";
    public const string High = "high";
}

/// <summary>
/// One row of D5' §14.4's per-type notification table, as data.
/// </summary>
/// <param name="Type">
/// The <c>notification_type</c>. It is also the key of the US-10.7 preference switch and of
/// <c>iam.users.notif_prefs</c>, which is why the spelling is the spec's verbatim and case-sensitive
/// (iam-svc's <c>LiteralKeyDictionaryConverter</c> exists to stop a JSON policy rewriting it).
/// </param>
/// <param name="Channel">§14.4's Channel column.</param>
/// <param name="TemplateKey">
/// The <c>content.notification_templates</c> key, or <see langword="null"/> for a message that
/// carries no user-visible string at all — the E-01 offer and the P-02 location request are silent
/// data messages the app renders from its own resources, and a template for either would be a
/// string nobody displays.
/// </param>
/// <param name="Priority">E-01 reserves <c>high</c> for the offer; D-33's SOS is the other one.</param>
/// <param name="Silent">
/// APNs <c>content-available: 1</c> with no alert — "silent, wakes app" (D6' §7.4).
/// </param>
/// <param name="Mutable">
/// Whether US-10.7 lets the recipient switch it off. False for the three iam-svc also refuses to
/// store (<c>SOS_TRIGGERED</c>, <c>SOS_RESOLVED</c>, <c>RIDE_CANCELLED</c>) — the two services must
/// agree, or a mute the profile accepted would be one this service ignores.
/// </param>
/// <param name="AcksExpected">
/// E-01's three-second contract. Only the offer has one, and it is what arms
/// <c>ack_deadline_at</c>.
/// </param>
/// <param name="FallbackTemplateKey">
/// What the SMS says when the push above went unacked. Only the offer has one.
/// </param>
/// <param name="DualGateway">
/// D-33: sent through the primary *and* the secondary gateway at the same time, taking whichever
/// lands first. One type, because two messages per event is a cost that only an emergency earns.
/// </param>
public sealed record NotificationTypeSpec(
    string Type,
    string Channel,
    string? TemplateKey,
    string Priority = NotificationPriorities.Normal,
    bool Silent = false,
    bool Mutable = true,
    bool AcksExpected = false,
    string? FallbackTemplateKey = null,
    bool DualGateway = false);

/// <summary>
/// D5' §14.4's table, transcribed. Nothing in this service decides a channel, a priority or a
/// template key in a branch — every one of them is looked up here.
/// </summary>
/// <remarks>
/// <para>
/// Three types are marked <b>Δ C051</b> below: §14.4 has no row for them, and each has a producer
/// that already emits the fact and a screen that already draws the consequence. They are recorded
/// as micro-change-sets in the C051 handoff.
/// </para>
/// <para>
/// <b>What is deliberately absent.</b> The AL-47 driver-QR prompt and its +5 min nudge, US-8.15's
/// refund notice and the P-14 COD reminder have no producer calling this service — fare-svc's
/// <c>QrNudgeSweeper</c> logs what it would send and says so — so they have no type and no seeded
/// template. Adding either without the other would be a key nobody resolves or a type nothing
/// sends.
/// </para>
/// </remarks>
public static class NotificationCatalogue
{
    // §14.4, row by row. --------------------------------------------------------------------

    /// <summary>
    /// The dispatch offer (E-01). High priority and silent on both platforms — FCM
    /// <c>priority=high</c> bypasses Doze, APNs <c>apns-priority: 10</c> with
    /// <c>content-available: 1</c> wakes the app — and the app draws SCR-DA-013 itself, which is why
    /// there is no template. Three seconds without an ack and <see cref="RideOfferSmsTemplate"/>
    /// goes out over SMS instead.
    /// </summary>
    public const string RideOffer = "RIDE_OFFER";

    /// <summary>The template the E-01 fallback SMS renders (migration 1904).</summary>
    public const string RideOfferSmsTemplate = "ride_offer_sms";

    public const string DriverAssigned = "DRIVER_ASSIGNED";
    public const string DriverArrived = "DRIVER_ARRIVED";
    public const string RideCancelled = "RIDE_CANCELLED";
    public const string ScheduledReminder = "SCHEDULED_REMINDER";
    public const string DirectionalExpiring = "DIRECTIONAL_EXPIRING";
    public const string LowBalance = "LOW_BALANCE";
    public const string PaymentConfirmed = "PAYMENT_CONFIRMED";

    /// <summary>P-02's data message. Lower case in §14.4, and kept that way — it is a wire value.</summary>
    public const string LocationRequest = "location_request";

    public const string PackagePickedUp = "package_picked_up";
    public const string PackageOnTheWay = "package_on_the_way";
    public const string PackageDelivered = "package_delivered";

    /// <summary>D-33. Both gateways, in parallel, p99 ≤ 5 s.</summary>
    public const string SosTriggered = "SOS_TRIGGERED";

    public const string SosResolved = "SOS_RESOLVED";

    public const string RegistrationApproved = "REGISTRATION_APPROVED";
    public const string RegistrationReviewRequired = "REGISTRATION_REVIEW_REQUIRED";

    // AL-44's two SMS links (D6' I-29.2). ----------------------------------------------------

    public const string ProxyRideLink = "PROXY_RIDE_LINK";
    public const string PickupConfirmLink = "PICKUP_CONFIRM_LINK";

    // Δ C051 — no §14.4 row, a producer that emits the fact, a screen that draws it. ----------

    /// <summary>DT-04 / US-6A.21: the Destination Filter stopped applying.</summary>
    public const string DirectionalCleared = "DIRECTIONAL_CLEARED";

    /// <summary>D5' §9.4's second clause — below zero is not "low", it is "top up before the next trip".</summary>
    public const string TopUpRequired = "TOP_UP_REQUIRED";

    /// <summary>D-13 / US-9.1: the one charge a driver does not initiate.</summary>
    public const string DailyFee = "DAILY_FEE";

    /// <summary>E-03's T−30/7/1 warnings.</summary>
    public const string DocumentExpiring = "DOCUMENT_EXPIRING";

    /// <summary>E-03's suspension.</summary>
    public const string DocumentExpired = "DOCUMENT_EXPIRED";

    /// <summary>US-14.8's announcement, pushed rather than only drawn as an in-app banner.</summary>
    public const string Broadcast = "BROADCAST";

    /// <summary>
    /// US-13.11: a fleet vehicle has not begun its booked departure. <b>Δ C059.</b>
    /// </summary>
    /// <remarks>
    /// <b>Not <see cref="ScheduledReminder"/>.</b> That one is dispatch-svc's courtesy *before* a
    /// booking — "your ride is in 30 minutes" (US-6A.15, US-10.9) — and this is an exception
    /// *after* a departure that should already have happened. Sharing a type would share a
    /// template, and a driver whose bus is ten minutes late would be told their ride is upcoming.
    /// High priority and unmutable for the same reason <see cref="RideCancelled"/> is: US-13.11
    /// calls it "a ringing alarm in the Android and iOS Driver Apps", and an alarm a driver can
    /// switch off in Settings is a notification, not an alarm.
    /// </remarks>
    public const string ScheduleNotStarted = "SCHEDULE_NOT_STARTED";

    private static readonly FrozenDictionary<string, NotificationTypeSpec> Specs = Build();

    /// <summary>Every type, ordered by name.</summary>
    public static IReadOnlyList<NotificationTypeSpec> All =>
        [.. Specs.Values.OrderBy(static spec => spec.Type, StringComparer.Ordinal)];

    /// <summary>The types US-10.7 refuses to let a recipient switch off.</summary>
    /// <remarks>
    /// Exactly iam-svc's set (<c>ProfileService.SafetyCritical</c>). Two services hold this list and
    /// they must agree: iam-svc drops such a key on the way in, this one ignores it on the way out,
    /// and a type in one list and not the other is a mute that appears to work and does not.
    /// </remarks>
    public static IReadOnlySet<string> SafetyCritical { get; } =
        new[] { SosTriggered, SosResolved, RideCancelled }.ToFrozenSet(StringComparer.Ordinal);

    public static bool TryGet(string? type, out NotificationTypeSpec spec)
    {
        if (!string.IsNullOrWhiteSpace(type) && Specs.TryGetValue(type, out var found))
        {
            spec = found;
            return true;
        }

        spec = null!;
        return false;
    }

    /// <summary>The spec for <paramref name="type"/>, or a throw. Callers hold a known type.</summary>
    public static NotificationTypeSpec Require(string type) =>
        TryGet(type, out var spec)
            ? spec
            : throw new ArgumentOutOfRangeException(
                nameof(type), type, "Unknown notification type; every type is declared in NotificationCatalogue.");

    private static FrozenDictionary<string, NotificationTypeSpec> Build()
    {
        NotificationTypeSpec[] specs =
        [
            // Trigger: ride offer to driver · FCM-hi / APNs silent · 3 s no-ack → SMS.
            new(RideOffer, NotificationChannels.Push, TemplateKey: null,
                Priority: NotificationPriorities.High, Silent: true,
                AcksExpected: true, FallbackTemplateKey: RideOfferSmsTemplate),

            new(DriverAssigned, NotificationChannels.Push, "driver_assigned"),
            new(DriverArrived, NotificationChannels.Push, "driver_arrived"),

            // Safety-critical: a passenger left waiting for a ride that no longer exists is the one
            // notification a preference may not suppress.
            new(RideCancelled, NotificationChannels.Push, "ride_cancelled", Mutable: false),

            new(ScheduledReminder, NotificationChannels.Push, "scheduled_reminder"),
            new(DirectionalExpiring, NotificationChannels.Push, "directional_expiring"),
            new(LowBalance, NotificationChannels.Push, "low_balance"),
            new(PaymentConfirmed, NotificationChannels.Push, "payment_confirmed"),

            // P-02: a silent data message carrying {kind, requestId, bookerName, ttl}. High
            // priority because the window is 300 s and a Dozing handset would spend most of it
            // asleep.
            new(LocationRequest, NotificationChannels.Push, TemplateKey: null,
                Priority: NotificationPriorities.High, Silent: true),

            // AL-21's two branches. The registered recipient gets a deep link into SCR-PA-021; the
            // unregistered one gets 1902's `package_on_the_way` SMS with a share-token link.
            new(PackagePickedUp, NotificationChannels.Push, "package_picked_up"),
            new(PackageOnTheWay, NotificationChannels.Sms, "package_on_the_way"),
            new(PackageDelivered, NotificationChannels.Push, "package_delivered"),

            // D-33. The only DualGateway type on the platform.
            new(SosTriggered, NotificationChannels.Sms, "sos_alert",
                Priority: NotificationPriorities.High, Mutable: false, DualGateway: true),
            new(SosResolved, NotificationChannels.Sms, "sos_alert", Mutable: false),

            new(RegistrationApproved, NotificationChannels.Push, "registration_approved"),
            new(RegistrationReviewRequired, NotificationChannels.Push, "registration_review_required"),

            // AL-44/AL-45. Both carry a token this service mints; neither can be muted, because the
            // recipient has no account to hold a preference on.
            new(ProxyRideLink, NotificationChannels.Sms, "proxy_ride_link", Mutable: false),
            new(PickupConfirmLink, NotificationChannels.Sms, "pickup_confirm_link", Mutable: false),

            // Δ C051.
            new(DirectionalCleared, NotificationChannels.Push, "directional_cleared"),
            new(TopUpRequired, NotificationChannels.Push, "top_up_required"),
            new(DailyFee, NotificationChannels.Push, "daily_fee_charged"),
            new(DocumentExpiring, NotificationChannels.Push, "document_expiring"),
            new(DocumentExpired, NotificationChannels.Push, "document_expired"),

            // The body is the broadcast's own trilingual message (content.broadcasts), resolved by
            // the caller — so there is no template key and the payload carries the text.
            new(Broadcast, NotificationChannels.Push, TemplateKey: null),

            // Δ C059. High priority so FCM bypasses Doze and APNs wakes a backgrounded app — a
            // departure alarm that arrives when the driver next unlocks their phone is not an
            // alarm. Not silent: unlike RIDE_OFFER there is no in-app screen waiting to draw it,
            // so the body is rendered from the template (migration 1905) in the recipient's own
            // language, which is also what the Fleet Portal's own members receive.
            new(ScheduleNotStarted, NotificationChannels.Push, "schedule_not_started",
                Priority: NotificationPriorities.High, Mutable: false),
        ];

        return specs.ToFrozenDictionary(static spec => spec.Type, StringComparer.Ordinal);
    }
}
