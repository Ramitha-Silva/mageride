using MageRide.Notification.Domain;
using MageRide.Notification.Messaging;
using MageRide.Notification.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Notification.Tests.Integration;

/// <summary>
/// D-26: every body is rendered in the recipient's language, and none of them is composed here.
/// </summary>
/// <remarks>
/// The component's fourth definition of done. The claim has two halves and both are asserted: the
/// language served is the recipient's (three users, three scripts, one event), and the text came
/// from content-svc rather than from this assembly — which is checked by <em>changing</em> the
/// template and watching the next send change with it.
/// </remarks>
[Collection(NotificationCollection.Name)]
public sealed class LanguageRenderingTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task Each_recipient_is_sent_their_own_language()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var sinhala = await harness.Seed.UserAsync("si");
        var tamil = await harness.Seed.UserAsync("ta");
        var english = await harness.Seed.UserAsync("en");

        foreach (var user in new[] { sinhala, tamil, english })
        {
            await harness.Seed.DeviceAsync(user.Id);
        }

        using var response = await harness.SendInternalAsync(new
        {
            notificationType = NotificationCatalogue.DriverAssigned,
            recipients = new[] { sinhala.Id, tamil.Id, english.Id },
        });

        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);

        await harness.DeliverAsync();

        Assert.Equal(3, harness.Pushes.Sent.Count);

        var rows = await harness.QueueAsync();

        // The language is resolved at enqueue and stored, so a preference change mid-retry cannot
        // produce two attempts in two languages.
        Assert.Equal("si", rows.Single(row => row.RecipientUserId == sinhala.Id).Language);
        Assert.Equal("ta", rows.Single(row => row.RecipientUserId == tamil.Id).Language);
        Assert.Equal("en", rows.Single(row => row.RecipientUserId == english.Id).Language);

        var bodies = harness.Pushes.Sent.Select(static push => push.Body!).ToArray();

        Assert.Contains(bodies, body => body.Contains("රියදුරෙකු", StringComparison.Ordinal));
        Assert.Contains(bodies, body => body.Contains("ஓட்டுநர்", StringComparison.Ordinal));
        Assert.Contains(bodies, body => body.Contains("A driver has accepted your ride.", StringComparison.Ordinal));

        // content-svc was asked for each language once, which is what the in-process cache buys.
        Assert.Contains("driver_assigned|si", harness.Content.Requests);
        Assert.Contains("driver_assigned|ta", harness.Content.Requests);
        Assert.Contains("driver_assigned|en", harness.Content.Requests);
    }

    /// <summary>
    /// The strings are content-svc's, not this service's: change the template and the next message
    /// changes. A body composed in C# would be unaffected, which is exactly what CLAUDE.md's
    /// trilingual rule forbids.
    /// </summary>
    [Fact]
    public async Task An_edited_template_changes_what_is_sent()
    {
        await using var harness = await NotificationHarness.StartAsync(
            postgres,
            redis,
            // No cache, so this asserts the render path rather than the invalidation path — that one
            // is content-svc's own definition of done and is exercised by the purge subscriber.
            new Dictionary<string, string?> { ["Notification:TemplateCacheTtl"] = "00:00:00" });

        var user = await harness.Seed.UserAsync("en");
        await harness.Seed.DeviceAsync(user.Id);

        harness.Content.Publish(
            "driver_assigned",
            new StubTemplate("නව මාතෘකාව", "නව සිංහල පණිවිඩය."),
            new StubTemplate("புதிய தலைப்பு", "புதிய தமிழ் செய்தி."),
            new StubTemplate("Edited title", "Edited body, straight from content-svc."));

        using var response = await harness.SendInternalAsync(new
        {
            notificationType = NotificationCatalogue.DriverAssigned,
            recipients = new[] { user.Id },
        });

        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);

        await harness.DeliverAsync();

        var push = Assert.Single(harness.Pushes.Sent);

        Assert.Equal("Edited title", push.Title);
        Assert.Equal("Edited body, straight from content-svc.", push.Body);
    }

    /// <summary>
    /// A template whose values are missing does not ship a sentence with a hole in it, and it is not
    /// retried either: the value will still be missing in five seconds.
    /// </summary>
    [Fact]
    public async Task A_message_whose_template_cannot_be_rendered_fails_rather_than_retrying()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        using var response = await harness.SendInternalAsync(new
        {
            notificationType = NotificationCatalogue.PackageOnTheWay,
            phones = new[] { "+94771234567" },

            // `package_on_the_way` interpolates {{link}} and nothing supplies it.
            data = new { rideId = Guid.NewGuid() },
        });

        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);

        await harness.DeliverAsync();

        var row = Assert.Single(await harness.QueueAsync());

        Assert.Equal(NotificationStatuses.Failed, row.Status);
        Assert.Empty(harness.AllSms);
    }

    /// <summary>
    /// An unknown key is a 404 from content-svc and a failed notification here — never an invented
    /// template. A key is only content once a migration has seeded it beside the code that sends it.
    /// </summary>
    [Fact]
    public async Task An_unknown_template_key_is_never_invented()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var user = await harness.Seed.UserAsync();
        await harness.Seed.DeviceAsync(user.Id);

        using var response = await harness.SendInternalAsync(new
        {
            notificationType = NotificationCatalogue.DriverAssigned,
            templateKey = "not_a_seeded_key",
            recipients = new[] { user.Id },
        });

        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);

        await harness.DeliverAsync();

        Assert.Equal(NotificationStatuses.Failed, (await harness.QueueAsync(user.Id)).Single().Status);
        Assert.Empty(harness.Pushes.Sent);
    }

    /// <summary>
    /// A ride event tells the booker and the rider of a proxy booking, each in their own language —
    /// P-05's fan-out and D-26's rule meeting on one event.
    /// </summary>
    [Fact]
    public async Task Both_sides_of_a_proxy_booking_are_told_in_their_own_language()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var booker = await harness.Seed.UserAsync("en");
        var rider = await harness.Seed.UserAsync("ta");

        await harness.Seed.DeviceAsync(booker.Id);
        await harness.Seed.DeviceAsync(rider.Id);

        var rideId = Guid.NewGuid();

        await harness.HandleAsync<RideEventHandler>(rideId.ToString(), "ride.cancelled", new
        {
            eventId = Guid.NewGuid(),
            eventType = "ride.cancelled",
            rideId,
            version = 4,
            ts = NotificationHarness.DefaultNow,
            payload = new
            {
                passengerId = booker.Id,
                bookerId = booker.Id,
                riderId = rider.Id,
                isProxy = true,
                state = "CancelledByRiderAfterAccept",
                kind = "proxy",
            },
        });

        await harness.DeliverAsync();

        Assert.Equal(2, harness.Pushes.Sent.Count);

        var bodies = harness.Pushes.Sent.Select(static push => push.Body!).ToArray();

        Assert.Contains(bodies, body => body.Contains("This ride has been cancelled.", StringComparison.Ordinal));
        Assert.Contains(bodies, body => body.Contains("ரத்து", StringComparison.Ordinal));
    }
}
