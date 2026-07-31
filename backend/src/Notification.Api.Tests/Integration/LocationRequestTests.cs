using MageRide.Notification.Domain;
using MageRide.Notification.Messaging;
using MageRide.Notification.Tests.Infrastructure;
using MageRide.Notification.Tokens;
using MageRide.TestKit;

namespace MageRide.Notification.Tests.Integration;

/// <summary>
/// P-02 / P-12 / AL-45: the proxy location-request round-trip's outbound half.
/// </summary>
/// <remarks>
/// The component's third definition of done — "a 6th location request in an hour from the same
/// booker is rejected" — measured against a real Redis, because P-12's bucket is per booker across
/// every replica and an in-process counter would let N replicas pass N × 5 an hour.
/// </remarks>
[Collection(NotificationCollection.Name)]
public sealed class LocationRequestTests(PostgresFixture postgres, RedisFixture redis)
{
    private static object Issued(
        Guid requestId, Guid bookerId, Guid? riderId, string state, string? riderPhone = null) => new
    {
        eventId = Guid.NewGuid(),
        eventType = "location.request.issued",
        requestId,
        ts = NotificationHarness.DefaultNow,
        payload = new
        {
            requestId,
            bookerId,
            riderId,
            riderPhone,
            state,
            issuedAt = NotificationHarness.DefaultNow,
            expiresAt = NotificationHarness.DefaultNow.AddSeconds(300),
        },
    };

    /// <summary>D6' §7.4's data message, and nothing a rider has to read.</summary>
    [Fact]
    public async Task A_registered_rider_gets_a_silent_high_priority_data_message()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var booker = await harness.Seed.UserAsync();
        var rider = await harness.Seed.UserAsync();
        await harness.Seed.DeviceAsync(rider.Id, "ios");

        var requestId = Guid.NewGuid();

        await harness.HandleAsync<RideEventHandler>(
            requestId.ToString(), "location.request.issued", Issued(requestId, booker.Id, rider.Id, "Pending"));

        await harness.DeliverAsync();

        var push = Assert.Single(harness.Pushes.Sent);

        Assert.True(push.Silent);
        Assert.Equal(NotificationPriorities.High, push.Priority);
        Assert.Equal("location_request", push.Data["kind"]);
        Assert.Equal(requestId.ToString(), push.Data["requestId"]);
        Assert.Equal("300", push.Data["ttl"]);

        // No token was minted: a registered rider answers in the app (AL-45's web path is for the
        // other branch).
        Assert.Empty(await harness.ShareTokensAsync());
    }

    /// <summary>The definition of done. Five pass, the sixth does not, and the reason names P-12.</summary>
    [Fact]
    public async Task A_sixth_location_request_in_an_hour_from_one_booker_is_refused()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var booker = await harness.Seed.UserAsync();
        var rider = await harness.Seed.UserAsync();
        await harness.Seed.DeviceAsync(rider.Id);

        for (var i = 0; i < 6; i++)
        {
            var requestId = Guid.NewGuid();

            await harness.HandleAsync<RideEventHandler>(
                requestId.ToString(), "location.request.issued", Issued(requestId, booker.Id, rider.Id, "Pending"));
        }

        await harness.DeliverAsync();

        var rows = await harness.QueueAsync(rider.Id);

        Assert.Equal(6, rows.Count);
        Assert.Equal(5, rows.Count(row => row.Status is NotificationStatuses.Sent));

        var refused = Assert.Single(rows, row => row.Status == NotificationStatuses.Suppressed);

        // The row records why, so "the rider never got my request" has an answer.
        Assert.Contains("P-12", refused.DedupeKey + await ReasonOf(harness, refused.Id), StringComparison.Ordinal);

        // And the rider's handset only ever buzzed five times.
        Assert.Equal(5, harness.Pushes.Sent.Count);
    }

    /// <summary>
    /// The bucket is spent <em>after</em> the dedupe claim, so a redelivered event costs nothing.
    /// Without that ordering, at-least-once delivery would quietly turn five requests an hour into
    /// four.
    /// </summary>
    [Fact]
    public async Task A_redelivered_location_request_does_not_spend_a_token()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var booker = await harness.Seed.UserAsync();
        var rider = await harness.Seed.UserAsync();
        await harness.Seed.DeviceAsync(rider.Id);

        var first = Guid.NewGuid();

        // One fact, delivered six times.
        for (var i = 0; i < 6; i++)
        {
            await harness.HandleAsync<RideEventHandler>(
                first.ToString(), "location.request.issued", Issued(first, booker.Id, rider.Id, "Pending"));
        }

        // Four more genuine requests still fit inside the hour: 1 + 4 = 5.
        for (var i = 0; i < 4; i++)
        {
            var requestId = Guid.NewGuid();

            await harness.HandleAsync<RideEventHandler>(
                requestId.ToString(), "location.request.issued", Issued(requestId, booker.Id, rider.Id, "Pending"));
        }

        await harness.DeliverAsync();

        var rows = await harness.QueueAsync(rider.Id);

        Assert.Equal(5, rows.Count);
        Assert.All(rows, row => Assert.Equal(NotificationStatuses.Sent, row.Status));
    }

    /// <summary>
    /// AL-45: <c>RiderNotRegistered</c> is not the end of the road. A <c>pickup_confirm</c> token is
    /// minted here, bound to the request's surrogate id, and SMSed — and returned to nobody.
    /// </summary>
    [Fact]
    public async Task An_unregistered_rider_is_smsed_a_pickup_confirm_link()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var booker = await harness.Seed.UserAsync();
        var requestId = await harness.Seed.LocationRequestAsync(booker.Id);

        await harness.HandleAsync<RideEventHandler>(
            requestId.ToString(),
            "location.request.issued",
            Issued(requestId, booker.Id, riderId: null, "RiderNotRegistered", "+94771111111"));

        await harness.DeliverAsync();

        var minted = Assert.Single(await harness.ShareTokensAsync());

        Assert.Equal(ShareTokenScopes.PickupConfirm, minted.Scope);
        Assert.Null(minted.TripId);
        Assert.NotNull(minted.LocationRequestId);

        var sms = Assert.Single(harness.AllSms);

        Assert.Equal("94771111111", sms.To);
        Assert.Contains(NotificationHarness.WebTrackBaseUrl, sms.Message, StringComparison.Ordinal);
        Assert.Contains(minted.Token, sms.Message, StringComparison.Ordinal);

        // No push: there is no account and no handset.
        Assert.Empty(harness.Pushes.Sent);
    }

    /// <summary>
    /// The token's deadline is the request's own. AL-45 pins it at 300 s and the contract pins the
    /// request's <c>ttl</c> at <c>const: 300</c> — a token that outlived the request would let a
    /// stranger answer a question nobody is still asking.
    /// </summary>
    [Fact]
    public async Task The_pickup_confirm_token_expires_with_the_request()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var booker = await harness.Seed.UserAsync();
        var requestId = await harness.Seed.LocationRequestAsync(booker.Id);

        await harness.HandleAsync<RideEventHandler>(
            requestId.ToString(),
            "location.request.issued",
            Issued(requestId, booker.Id, riderId: null, "RiderNotRegistered", "+94771111111"));

        await using var connection = await harness.OpenAsync();

        var expiresAt = await Dapper.SqlMapper.QuerySingleAsync<DateTimeOffset>(
            connection,
            "SELECT expires_at FROM safety.trip_share_tokens WHERE scope = 'pickup_confirm';");

        Assert.Equal(NotificationHarness.DefaultNow.AddSeconds(300), expiresAt);
    }

    /// <summary>
    /// P-12 meters the booker, not the rider being asked. A rider pinged by five different bookers
    /// has done nothing wrong, and refusing them would make the limit punish the wrong person.
    /// </summary>
    [Fact]
    public async Task The_limit_is_per_booker_not_per_rider()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var rider = await harness.Seed.UserAsync();
        await harness.Seed.DeviceAsync(rider.Id);

        for (var i = 0; i < 6; i++)
        {
            var booker = await harness.Seed.UserAsync();
            var requestId = Guid.NewGuid();

            await harness.HandleAsync<RideEventHandler>(
                requestId.ToString(), "location.request.issued", Issued(requestId, booker.Id, rider.Id, "Pending"));
        }

        await harness.DeliverAsync();

        var rows = await harness.QueueAsync(rider.Id);

        Assert.Equal(6, rows.Count);
        Assert.All(rows, row => Assert.Equal(NotificationStatuses.Sent, row.Status));
    }

    private static async Task<string> ReasonOf(NotificationHarness harness, Guid id)
    {
        await using var connection = await harness.OpenAsync();

        return await Dapper.SqlMapper.QuerySingleAsync<string>(
            connection, "SELECT last_error FROM comms.notifications WHERE id = @Id;", new { Id = id });
    }
}
