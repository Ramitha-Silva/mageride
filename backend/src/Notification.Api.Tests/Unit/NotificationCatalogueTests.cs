using MageRide.Notification.Domain;

namespace MageRide.Notification.Tests.Unit;

/// <summary>
/// D5' §14.4's per-type table, compared with a hand transcription of the spec.
/// </summary>
/// <remarks>
/// <b>The comparison fails both ways.</b> A type this service invented and a §14.4 row it dropped
/// are equally loud, which is the same shape fare-svc's <c>The_machine_is_exactly_the_diagram</c>
/// takes for D5' §8.1. The five Δ C051 additions are listed separately and asserted to be exactly
/// five, so a sixth cannot be added without saying so here.
/// </remarks>
public sealed class NotificationCatalogueTests
{
    /// <summary>§14.4's Trigger/Channel/Type/Throttle table, transcribed from the spec.</summary>
    private static readonly (string Type, string Channel)[] SpecTable =
    [
        ("RIDE_OFFER", NotificationChannels.Push),
        ("DRIVER_ASSIGNED", NotificationChannels.Push),
        ("DRIVER_ARRIVED", NotificationChannels.Push),
        ("RIDE_CANCELLED", NotificationChannels.Push),
        ("SCHEDULED_REMINDER", NotificationChannels.Push),
        ("DIRECTIONAL_EXPIRING", NotificationChannels.Push),
        ("LOW_BALANCE", NotificationChannels.Push),
        ("PAYMENT_CONFIRMED", NotificationChannels.Push),
        ("location_request", NotificationChannels.Push),
        ("package_picked_up", NotificationChannels.Push),
        ("package_delivered", NotificationChannels.Push),
        ("SOS_TRIGGERED", NotificationChannels.Sms),
        ("REGISTRATION_APPROVED", NotificationChannels.Push),
    ];

    /// <summary>What this component added, each with the spec line that names the fact.</summary>
    private static readonly string[] AdditionsWithNoSpecRow =
    [
        "DIRECTIONAL_CLEARED",      // DT-04 / US-6A.21 — dispatch-svc emits `directional.cleared`
        "TOP_UP_REQUIRED",          // D5' §9.4's second clause; wallet-svc's `severity`
        "DAILY_FEE",                // D-13 / US-9.1; wallet.debited kind='daily_fee'
        "DOCUMENT_EXPIRING",        // E-03
        "DOCUMENT_EXPIRED",         // E-03
        "BROADCAST",                // US-14.8
        "SOS_RESOLVED",             // US-12.11's ack; iam-svc already refuses to let it be muted
        "PROXY_RIDE_LINK",          // AL-44 / D6' I-29.2
        "PICKUP_CONFIRM_LINK",      // AL-45 / D6' I-29.2
        "package_on_the_way",       // AL-21's unregistered branch; 1902 seeds the key

        // §14.4 has one REGISTRATION_RESULT row and US-2.14 has two outcomes. AL-27's auto-approval
        // and AL-29/AL-30's "an officer has to look at this" are different facts to the driver
        // waiting, and registry-svc emits them as two events — so the row is split rather than
        // rendered from a branch inside one template.
        "REGISTRATION_REVIEW_REQUIRED",
    ];

    [Fact]
    public void Every_spec_row_has_a_type_on_the_channel_the_spec_names()
    {
        foreach (var (type, channel) in SpecTable)
        {
            Assert.True(NotificationCatalogue.TryGet(type, out var spec), $"§14.4 names {type} and it is missing.");
            Assert.Equal(channel, spec.Channel);
        }
    }

    [Fact]
    public void The_catalogue_invents_nothing_beyond_the_declared_additions()
    {
        var expected = SpecTable.Select(static row => row.Type).Concat(AdditionsWithNoSpecRow).ToHashSet(StringComparer.Ordinal);
        var actual = NotificationCatalogue.All.Select(static spec => spec.Type).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected.OrderBy(static t => t, StringComparer.Ordinal), actual.OrderBy(static t => t, StringComparer.Ordinal));
    }

    /// <summary>
    /// E-01, in one assertion: high priority (FCM bypasses Doze), silent (APNs
    /// <c>content-available: 1</c> wakes the app instead of drawing a banner), an ack contract, and
    /// something to fall back to.
    /// </summary>
    [Fact]
    public void The_offer_push_is_high_priority_silent_and_has_a_fallback()
    {
        var offer = NotificationCatalogue.Require(NotificationCatalogue.RideOffer);

        Assert.Equal(NotificationPriorities.High, offer.Priority);
        Assert.True(offer.Silent);
        Assert.True(offer.AcksExpected);
        Assert.Equal("ride_offer_sms", offer.FallbackTemplateKey);

        // And no template of its own: the driver app draws SCR-DA-013 from the data message.
        Assert.Null(offer.TemplateKey);
    }

    /// <summary>P-02's message is a data message too — the prompt is the rider app's own screen.</summary>
    [Fact]
    public void The_location_request_is_a_silent_high_priority_data_message()
    {
        var request = NotificationCatalogue.Require(NotificationCatalogue.LocationRequest);

        Assert.Equal(NotificationPriorities.High, request.Priority);
        Assert.True(request.Silent);
        Assert.Null(request.TemplateKey);
        Assert.False(request.AcksExpected);
    }

    /// <summary>D-33: one type pays for two messages, and it is the emergency.</summary>
    [Fact]
    public void Only_the_sos_uses_both_gateways_in_parallel()
    {
        Assert.Equal(
            [NotificationCatalogue.SosTriggered],
            NotificationCatalogue.All.Where(static spec => spec.DualGateway).Select(static spec => spec.Type));
    }

    /// <summary>
    /// The set iam-svc's <c>ProfileService</c> also refuses to store. Two services hold this list and
    /// a type in one and not the other is a mute that appears to work and does not.
    /// </summary>
    [Fact]
    public void The_unmutable_set_is_exactly_the_three_iam_svc_drops()
    {
        Assert.Equal(
            ["RIDE_CANCELLED", "SOS_RESOLVED", "SOS_TRIGGERED"],
            NotificationCatalogue.SafetyCritical.OrderBy(static t => t, StringComparer.Ordinal));
    }

    /// <summary>
    /// A recipient with no account holds no preferences, so anything addressed to a number has to be
    /// immutable — otherwise the switch would silently apply to nobody.
    /// </summary>
    [Fact]
    public void Nothing_sent_to_an_unregistered_recipient_is_mutable()
    {
        foreach (var type in new[]
                 {
                     NotificationCatalogue.ProxyRideLink,
                     NotificationCatalogue.PickupConfirmLink,
                     NotificationCatalogue.SosTriggered,
                 })
        {
            Assert.False(NotificationCatalogue.Require(type).Mutable, $"{type} must not be mutable.");
        }
    }

    /// <summary>Every type that renders words names a key; the three that do not are the data ones.</summary>
    [Fact]
    public void Only_the_data_messages_have_no_template()
    {
        var withoutTemplate = NotificationCatalogue.All
            .Where(static spec => spec.TemplateKey is null)
            .Select(static spec => spec.Type)
            .OrderBy(static t => t, StringComparer.Ordinal);

        Assert.Equal(["BROADCAST", "RIDE_OFFER", "location_request"], withoutTemplate);
    }

    [Fact]
    public void An_unknown_type_is_a_throw_rather_than_a_guess() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => NotificationCatalogue.Require("NOT_A_TYPE"));
}

/// <summary>The claim that turns at-least-once delivery into one message.</summary>
public sealed class DedupeKeyTests
{
    /// <summary>
    /// One <c>ride.accepted</c> tells the booker and the rider. Without the recipient in the key the
    /// second would collide with the first and one of two people would hear nothing.
    /// </summary>
    [Fact]
    public void Two_recipients_of_one_event_get_two_keys()
    {
        var ride = Guid.NewGuid().ToString();
        var booker = Guid.NewGuid();
        var rider = Guid.NewGuid();

        Assert.NotEqual(
            NotificationDedupe.For("ride", ride, "DRIVER_ASSIGNED", booker),
            NotificationDedupe.For("ride", ride, "DRIVER_ASSIGNED", rider));
    }

    /// <summary>And the same fact redelivered gets the same one, which is the whole point.</summary>
    [Fact]
    public void The_same_fact_redelivered_gets_the_same_key()
    {
        var ride = Guid.NewGuid().ToString();
        var booker = Guid.NewGuid();

        Assert.Equal(
            NotificationDedupe.For("ride", ride, "DRIVER_ASSIGNED", booker),
            NotificationDedupe.For("ride", ride, "DRIVER_ASSIGNED", booker));
    }

    /// <summary>Two types about one aggregate are two notifications, not a redelivery.</summary>
    [Fact]
    public void Two_types_about_one_aggregate_get_two_keys()
    {
        var ride = Guid.NewGuid().ToString();

        Assert.NotEqual(
            NotificationDedupe.For("ride", ride, "DRIVER_ASSIGNED"),
            NotificationDedupe.For("ride", ride, "DRIVER_ARRIVED"));
    }

    /// <summary>The E-01 fallback is keyed by the push it replaces, so one push buys one SMS.</summary>
    [Fact]
    public void A_fallback_is_keyed_by_the_push_it_replaces()
    {
        var push = Guid.NewGuid();

        Assert.Equal(NotificationDedupe.Fallback(push), NotificationDedupe.Fallback(push));
        Assert.NotEqual(NotificationDedupe.Fallback(push), NotificationDedupe.Fallback(Guid.NewGuid()));
    }
}

/// <summary>US-10.7's switch, applied.</summary>
public sealed class PreferenceTests
{
    private static NotificationRecipient With(params (string Type, bool Enabled)[] preferences) =>
        new(Guid.NewGuid(), "+94771234567", "en",
            preferences.ToDictionary(static p => p.Type, static p => p.Enabled, StringComparer.Ordinal));

    [Fact]
    public void A_type_nobody_has_touched_is_on() =>
        Assert.True(With().Accepts(NotificationCatalogue.Require(NotificationCatalogue.LowBalance)));

    [Fact]
    public void A_muted_type_is_refused() =>
        Assert.False(With((NotificationCatalogue.LowBalance, false))
            .Accepts(NotificationCatalogue.Require(NotificationCatalogue.LowBalance)));

    /// <summary>
    /// A passenger who muted cancellations would be left waiting for a car that is not coming. The
    /// switch is accepted by the write path and ignored here — the same thing iam-svc does.
    /// </summary>
    [Fact]
    public void A_safety_critical_type_is_sent_even_when_it_has_been_muted()
    {
        Assert.True(With((NotificationCatalogue.RideCancelled, false))
            .Accepts(NotificationCatalogue.Require(NotificationCatalogue.RideCancelled)));

        Assert.True(With((NotificationCatalogue.SosTriggered, false))
            .Accepts(NotificationCatalogue.Require(NotificationCatalogue.SosTriggered)));
    }

    [Fact]
    public void An_unregistered_recipient_holds_no_preferences_and_accepts_everything()
    {
        var anonymous = NotificationRecipient.Anonymous("+94771234567");

        Assert.Empty(anonymous.Preferences);
        Assert.True(anonymous.Accepts(NotificationCatalogue.Require(NotificationCatalogue.PackageOnTheWay)));
    }
}
