using MageRide.Notification.Domain;
using MageRide.Notification.Messaging;
using MageRide.Notification.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Notification.Tests.Integration;

/// <summary>
/// E-01: the offer push, and the SMS that replaces it when nobody acked.
/// </summary>
/// <remarks>
/// The component's first definition of done — "an offer push that is not acked within 3 s triggers
/// the SMS fallback <b>exactly once</b>" — is two database facts rather than one worker's care, and
/// each is asserted separately: the claim (<c>Sent → FellBackToSms</c> in the statement that selects
/// the row) and the second guard (<c>ux_notifications_dedupe</c> on <c>fallback:{pushId}</c>).
/// </remarks>
[Collection(NotificationCollection.Name)]
public sealed class OfferFallbackTests(PostgresFixture postgres, RedisFixture redis)
{
    private static object Offer(Guid rideId, Guid offerId, Guid driverId) => new
    {
        eventType = "offer.created",
        eventId = Guid.NewGuid(),
        rideId,
        offerId,
        driverId,
        version = 1,
        ts = NotificationHarness.DefaultNow,
        expiresAt = NotificationHarness.DefaultNow.AddSeconds(15),
        fareEstimateMinor = 48_000L,
        currency = "LKR",
        distanceToPickupM = 1_400L,
        paymentMethod = "cash",
    };

    /// <summary>
    /// The push itself, before anything falls back: high priority, silent, and carrying the two
    /// values the fallback SMS will need — because both are rendered from the same payload.
    /// </summary>
    [Fact]
    public async Task An_offer_is_pushed_high_priority_and_silent()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.UserAsync(role: "driver");
        await harness.Seed.DeviceAsync(driver.Id);

        await harness.HandleAsync<DispatchEventHandler>(
            Guid.NewGuid().ToString(), "offer.created", Offer(Guid.NewGuid(), Guid.NewGuid(), driver.Id));

        await harness.DeliverAsync();

        var push = Assert.Single(harness.Pushes.Sent);

        Assert.Equal(NotificationPriorities.High, push.Priority);
        Assert.True(push.Silent);
        Assert.Null(push.Title);
        Assert.Equal("ride_offer", push.Data["kind"]);
        Assert.Equal("480.00", push.Data["fare"]);
        Assert.Equal("1.4", push.Data["distance"]);

        var row = Assert.Single(await harness.QueueAsync(driver.Id));

        Assert.Equal(NotificationStatuses.Sent, row.Status);
        Assert.Equal(NotificationHarness.DefaultNow.AddSeconds(3), row.AckDeadlineAt);
    }

    /// <summary>The definition of done, in one test: three seconds, one SMS, however many sweeps.</summary>
    [Fact]
    public async Task An_unacked_offer_falls_back_to_sms_exactly_once()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.UserAsync(role: "driver", language: "si");
        await harness.Seed.DeviceAsync(driver.Id);

        await harness.HandleAsync<DispatchEventHandler>(
            Guid.NewGuid().ToString(), "offer.created", Offer(Guid.NewGuid(), Guid.NewGuid(), driver.Id));

        await harness.DeliverAsync();

        // Two seconds in, the window is still open and nothing falls back.
        harness.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0, await harness.SweepUnackedOffersAsync());
        Assert.Empty(harness.AllSms);

        // Past three, exactly one SMS is enqueued — and a second, third and fourth sweep add none.
        harness.Clock.Advance(TimeSpan.FromSeconds(1.5));

        Assert.Equal(1, await harness.SweepUnackedOffersAsync());
        Assert.Equal(0, await harness.SweepUnackedOffersAsync());
        Assert.Equal(0, await harness.SweepUnackedOffersAsync());

        await harness.DeliverAsync();

        var sms = Assert.Single(harness.AllSms);

        // Rendered in the driver's own language, from the same payload the push carried.
        Assert.Equal(driver.Phone.TrimStart('+'), sms.To);
        Assert.Contains("නව MageRide", sms.Message, StringComparison.Ordinal);
        Assert.Contains("480.00", sms.Message, StringComparison.Ordinal);
        Assert.Contains("1.4", sms.Message, StringComparison.Ordinal);

        var rows = await harness.QueueAsync(driver.Id);

        Assert.Equal(2, rows.Count);

        var push = rows.Single(row => row.Channel == NotificationChannels.Push);
        var fallback = rows.Single(row => row.Channel == NotificationChannels.Sms);

        Assert.Equal(NotificationStatuses.FellBackToSms, push.Status);
        Assert.Equal(NotificationStatuses.Sent, fallback.Status);
        Assert.Equal(push.Id, fallback.FallbackOf);

        // The support reader looking for "why did this driver get an SMS" finds the offer.
        Assert.Equal(NotificationCatalogue.RideOffer, fallback.NotificationType);
    }

    /// <summary>A handset that woke up in time costs nothing — no sweep, no SMS, no bill.</summary>
    [Fact]
    public async Task An_acked_offer_never_falls_back()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.UserAsync(role: "driver");
        await harness.Seed.DeviceAsync(driver.Id);

        await harness.HandleAsync<DispatchEventHandler>(
            Guid.NewGuid().ToString(), "offer.created", Offer(Guid.NewGuid(), Guid.NewGuid(), driver.Id));

        await harness.DeliverAsync();

        var push = Assert.Single(await harness.QueueAsync(driver.Id));

        harness.Clock.Advance(TimeSpan.FromSeconds(1));

        using var acked = await harness.PostAsync(
            "/v1/notify/ack", new { notificationId = push.Id }, harness.Tokens.Driver(driver.Id));

        Assert.Equal(System.Net.HttpStatusCode.NoContent, acked.StatusCode);

        harness.Clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(0, await harness.SweepUnackedOffersAsync());
        await harness.DeliverAsync();

        Assert.Empty(harness.AllSms);
        Assert.Equal(NotificationStatuses.Acked, (await harness.QueueAsync(driver.Id)).Single().Status);
    }

    /// <summary>
    /// An ack that arrives after the sweep has already fallen back changes nothing. The driver gets
    /// both messages once, which is the honest outcome of a slow handset — an ack cannot un-send an
    /// SMS.
    /// </summary>
    [Fact]
    public async Task A_late_ack_does_not_recall_the_sms()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.UserAsync(role: "driver");
        await harness.Seed.DeviceAsync(driver.Id);

        await harness.HandleAsync<DispatchEventHandler>(
            Guid.NewGuid().ToString(), "offer.created", Offer(Guid.NewGuid(), Guid.NewGuid(), driver.Id));

        await harness.DeliverAsync();

        var push = Assert.Single(await harness.QueueAsync(driver.Id));

        harness.Clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(1, await harness.SweepUnackedOffersAsync());

        using var late = await harness.PostAsync(
            "/v1/notify/ack", new { notificationId = push.Id }, harness.Tokens.Driver(driver.Id));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, late.StatusCode);
    }

    /// <summary>An ack is a claim about your own notification, and the guard is in the statement.</summary>
    [Fact]
    public async Task Another_driver_cannot_ack_somebody_elses_offer()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.UserAsync(role: "driver");
        var stranger = await harness.Seed.UserAsync(role: "driver");
        await harness.Seed.DeviceAsync(driver.Id);

        await harness.HandleAsync<DispatchEventHandler>(
            Guid.NewGuid().ToString(), "offer.created", Offer(Guid.NewGuid(), Guid.NewGuid(), driver.Id));

        await harness.DeliverAsync();

        var push = Assert.Single(await harness.QueueAsync(driver.Id));

        using var response = await harness.PostAsync(
            "/v1/notify/ack", new { notificationId = push.Id }, harness.Tokens.Driver(stranger.Id));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(NotificationStatuses.Sent, (await harness.QueueAsync(driver.Id)).Single().Status);
    }

    /// <summary>
    /// D6' §2.3 is at-least-once, so a redelivered <c>offer.created</c> is the normal case on a
    /// consumer restart — and it must not buzz the driver twice.
    /// </summary>
    [Fact]
    public async Task A_redelivered_offer_pushes_once()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.UserAsync(role: "driver");
        await harness.Seed.DeviceAsync(driver.Id);

        var rideId = Guid.NewGuid();
        var offerId = Guid.NewGuid();

        // Two deliveries of the same fact, with different event ids — which is what a producer that
        // rewrote its outbox row would look like.
        await harness.HandleAsync<DispatchEventHandler>(rideId.ToString(), "offer.created", Offer(rideId, offerId, driver.Id));
        await harness.HandleAsync<DispatchEventHandler>(rideId.ToString(), "offer.created", Offer(rideId, offerId, driver.Id));

        await harness.DeliverAsync();

        Assert.Single(harness.Pushes.Sent);
        Assert.Single(await harness.QueueAsync(driver.Id));
    }

    /// <summary>
    /// A driver with no registered handset cannot be pushed to, and there is no push to go unacked
    /// — so the row fails rather than waiting for a fallback that E-01 never arms.
    /// </summary>
    [Fact]
    public async Task An_offer_to_a_driver_with_no_device_fails_rather_than_retrying_forever()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.UserAsync(role: "driver");

        await harness.HandleAsync<DispatchEventHandler>(
            Guid.NewGuid().ToString(), "offer.created", Offer(Guid.NewGuid(), Guid.NewGuid(), driver.Id));

        await harness.DeliverAsync();

        var row = Assert.Single(await harness.QueueAsync(driver.Id));

        Assert.Equal(NotificationStatuses.Failed, row.Status);
        Assert.Empty(harness.AllSms);
    }
}
