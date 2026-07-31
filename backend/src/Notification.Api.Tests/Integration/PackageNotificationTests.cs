using System.Text.Json;
using MageRide.Notification.Domain;
using MageRide.Notification.Messaging;
using MageRide.Notification.Tests.Infrastructure;
using MageRide.Notification.Tokens;
using MageRide.TestKit;

namespace MageRide.Notification.Tests.Integration;

/// <summary>
/// AL-21 / P-09: the package recipient's two branches, and the fence that separates them from every
/// client API.
/// </summary>
[Collection(NotificationCollection.Name)]
public sealed class PackageNotificationTests(PostgresFixture postgres, RedisFixture redis)
{
    private static object PickedUp(Guid rideId, Guid senderId, string? recipientPhone, string? deliveryOtp) => new
    {
        eventId = Guid.NewGuid(),
        eventType = "package.picked_up",
        rideId,
        version = 3,
        ts = NotificationHarness.DefaultNow,
        payload = new
        {
            passengerId = senderId,
            state = "InProgress",
            packageStatus = "PickedUp",
            packageSize = "S",
            recipientName = "Kamala",
            recipientPhone,
            paymentMethod = "cash",
            deliveryOtp,
        },
    };

    /// <summary>
    /// Registered: a high-priority FCM deep link into SCR-PA-021, carrying the delivery code the app
    /// is a safe place to show.
    /// </summary>
    [Fact]
    public async Task A_registered_recipient_gets_a_deep_link_push()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var sender = await harness.Seed.UserAsync();
        var recipient = await harness.Seed.UserAsync("ta");
        await harness.Seed.DeviceAsync(recipient.Id);

        var rideId = Guid.NewGuid();

        await harness.HandleAsync<RideEventHandler>(
            rideId.ToString(), "package.picked_up", PickedUp(rideId, sender.Id, recipient.Phone, "4821"));

        await harness.DeliverAsync();

        var push = Assert.Single(harness.Pushes.Sent);

        Assert.Equal($"mageride://package/{rideId}", push.Data["deeplink"]);
        Assert.Equal("4821", push.Data["deliveryOtp"]);
        Assert.Contains("பொதி", push.Body!, StringComparison.Ordinal);

        // No token and no SMS: the recipient has an app.
        Assert.Empty(await harness.ShareTokensAsync());
        Assert.Empty(harness.AllSms);
    }

    /// <summary>
    /// Unregistered: an SMS carrying a <c>package_recipient</c> token, and <b>not</b> the delivery
    /// code — D6' I-23.3 has the web page show that after the token validates, which is what makes
    /// the token worth having.
    /// </summary>
    [Fact]
    public async Task An_unregistered_recipient_is_smsed_a_share_link_without_the_otp()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var sender = await harness.Seed.UserAsync();
        var rideId = Guid.NewGuid();

        await harness.HandleAsync<RideEventHandler>(
            rideId.ToString(), "package.picked_up", PickedUp(rideId, sender.Id, "+94769999999", "4821"));

        await harness.DeliverAsync();

        var minted = Assert.Single(await harness.ShareTokensAsync());

        Assert.Equal(ShareTokenScopes.PackageRecipient, minted.Scope);
        Assert.Equal(rideId, minted.TripId);

        var sms = Assert.Single(harness.AllSms);

        Assert.Equal("94769999999", sms.To);
        Assert.Contains(minted.Token, sms.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("4821", sms.Message, StringComparison.Ordinal);

        Assert.Empty(harness.Pushes.Sent);
    }

    /// <summary>
    /// The component's first fence. A token is minted server-side and put in an SMS; no response
    /// body on this service carries one, on any route.
    /// </summary>
    [Fact]
    public async Task A_share_token_never_leaves_through_an_api()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var sender = await harness.Seed.UserAsync();
        var rideId = Guid.NewGuid();

        await harness.HandleAsync<RideEventHandler>(
            rideId.ToString(), "package.picked_up", PickedUp(rideId, sender.Id, "+94769999999", "4821"));

        await harness.DeliverAsync();

        var minted = Assert.Single(await harness.ShareTokensAsync());

        // Every response this service can produce, checked for the token that was just minted.
        var bodies = new List<string>();

        using (var send = await harness.SendInternalAsync(new
               {
                   notificationType = NotificationCatalogue.PackageDelivered,
                   recipients = new[] { sender.Id },
               }))
        {
            bodies.Add(await send.Content.ReadAsStringAsync());
        }

        using (var preferences = await harness.SetPreferencesAsync(
                   new Dictionary<string, bool> { ["LOW_BALANCE"] = false },
                   harness.Tokens.Passenger(sender.Id)))
        {
            bodies.Add(await preferences.Content.ReadAsStringAsync());
        }

        using (var token = await harness.PostAsync(
                   "/v1/notify/register-token",
                   new { token = "fcm-abc", platform = "android", deviceId = "dev-1" },
                   harness.Tokens.Passenger(sender.Id)))
        {
            bodies.Add(await token.Content.ReadAsStringAsync());
        }

        foreach (var body in bodies)
        {
            Assert.DoesNotContain(minted.Token, body, StringComparison.Ordinal);
        }

        // It is in the SMS, which is the only place it is supposed to be.
        Assert.Contains(minted.Token, harness.AllSms.Single().Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The token's TTL is "delivery + 1 h" (D6' I-23.3), which this service reads as a configured
    /// ceiling — the ride's own completion is what safety-svc revokes on.
    /// </summary>
    [Fact]
    public async Task The_package_token_carries_the_configured_ttl()
    {
        await using var harness = await NotificationHarness.StartAsync(
            postgres,
            redis,
            new Dictionary<string, string?> { ["Notification:PackageRecipientTokenTtl"] = "02:00:00" });

        var sender = await harness.Seed.UserAsync();
        var rideId = Guid.NewGuid();

        await harness.HandleAsync<RideEventHandler>(
            rideId.ToString(), "package.picked_up", PickedUp(rideId, sender.Id, "+94769999999", null));

        await using var connection = await harness.OpenAsync();

        var expiresAt = await Dapper.SqlMapper.QuerySingleAsync<DateTimeOffset>(
            connection, "SELECT expires_at FROM safety.trip_share_tokens WHERE scope = 'package_recipient';");

        Assert.Equal(NotificationHarness.DefaultNow.AddHours(2), expiresAt);
    }

    /// <summary>Delivery tells the sender, and the recipient too when they have an account (US-10.13).</summary>
    [Fact]
    public async Task A_delivery_tells_the_sender_and_a_registered_recipient()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var sender = await harness.Seed.UserAsync();
        var recipient = await harness.Seed.UserAsync();

        await harness.Seed.DeviceAsync(sender.Id);
        await harness.Seed.DeviceAsync(recipient.Id);

        var rideId = Guid.NewGuid();

        await harness.HandleAsync<RideEventHandler>(rideId.ToString(), "package.delivered", new
        {
            eventId = Guid.NewGuid(),
            eventType = "package.delivered",
            rideId,
            version = 5,
            ts = NotificationHarness.DefaultNow,
            payload = new
            {
                passengerId = sender.Id,
                state = "Completed",
                packageStatus = "Delivered",
                recipientPhone = recipient.Phone,
                paymentMethod = "cash",
            },
        });

        await harness.DeliverAsync();

        Assert.Equal(2, harness.Pushes.Sent.Count);
        Assert.Equal(2, (await harness.QueueAsync()).Count);
    }

    /// <summary>
    /// AL-44 / US-8.22: a driver accepting a proxy booking SMSes the rider a <c>proxy_rider</c> link.
    /// </summary>
    [Fact]
    public async Task A_proxy_accept_mints_a_proxy_rider_link()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var booker = await harness.Seed.UserAsync();
        var rider = await harness.Seed.UserAsync();
        var rideId = Guid.NewGuid();

        await harness.HandleAsync<RideEventHandler>(rideId.ToString(), "ride.accepted", new
        {
            eventId = Guid.NewGuid(),
            eventType = "ride.accepted",
            rideId,
            version = 2,
            ts = NotificationHarness.DefaultNow,
            payload = new
            {
                passengerId = booker.Id,
                bookerId = booker.Id,
                riderId = rider.Id,
                driverId = Guid.NewGuid(),
                isProxy = true,
                kind = "proxy",
                state = "Accepted",
                riderName = "Kamala",
            },
        });

        await harness.DeliverAsync();

        var minted = Assert.Single(await harness.ShareTokensAsync());

        Assert.Equal(ShareTokenScopes.ProxyRider, minted.Scope);
        Assert.Equal(rideId, minted.TripId);

        var sms = Assert.Single(harness.AllSms);

        Assert.Equal(rider.Phone.TrimStart('+'), sms.To);
        Assert.Contains(minted.Token, sms.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// With no <c>WebTrackBaseUrl</c> there is no link to send, and the message is refused rather
    /// than sent with a broken URL — the recipient of every one of these has no app to find another
    /// way in.
    /// </summary>
    [Fact]
    public async Task Without_a_web_base_url_the_share_sms_is_refused_rather_than_broken()
    {
        await using var harness = await NotificationHarness.StartAsync(
            postgres, redis, new Dictionary<string, string?> { ["Notification:WebTrackBaseUrl"] = string.Empty });

        var sender = await harness.Seed.UserAsync();
        var rideId = Guid.NewGuid();

        await harness.HandleAsync<RideEventHandler>(
            rideId.ToString(), "package.picked_up", PickedUp(rideId, sender.Id, "+94769999999", null));

        await harness.DeliverAsync();

        Assert.Empty(await harness.ShareTokensAsync());
        Assert.Empty(harness.AllSms);
        Assert.Empty(await harness.QueueAsync());
    }

    /// <summary>
    /// The internal plane sends; an open one would be a free SMS gateway into every handset on the
    /// platform. A caller with no key gets the gateway's own answer for the prefix.
    /// </summary>
    [Fact]
    public async Task The_internal_send_route_refuses_a_caller_with_no_key()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        using var response = await harness.SendInternalAsync(
            new { notificationType = NotificationCatalogue.DriverAssigned, phones = new[] { "+94771234567" } },
            apiKey: null);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);

        var (code, _) = await NotificationHarness.ProblemAsync(response);
        Assert.Equal("not-found", code);
    }

    /// <summary>A send with no recipients at all is a caller bug, not an empty fan-out.</summary>
    [Fact]
    public async Task A_send_with_no_recipients_is_refused()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        using var response = await harness.SendInternalAsync(
            new { notificationType = NotificationCatalogue.DriverAssigned });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);

        var (code, body) = await NotificationHarness.ProblemAsync(response);

        Assert.Equal("validation-failed", code);
        Assert.Equal(JsonValueKind.Object, body.GetProperty("errors").ValueKind);
    }
}
