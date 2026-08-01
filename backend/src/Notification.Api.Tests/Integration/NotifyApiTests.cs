using System.Net;
using Dapper;
using MageRide.Notification.Domain;
using MageRide.Notification.Endpoints;
using MageRide.Notification.Messaging;
using MageRide.Notification.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Notification.Tests.Integration;

/// <summary>
/// The two bearer routes D3' declares: register a device, set per-type preferences (US-10.7).
/// </summary>
[Collection(NotificationCollection.Name)]
public sealed class NotifyApiTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task A_registered_token_is_what_a_push_is_addressed_to()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var user = await harness.Seed.UserAsync();

        using var registered = await harness.PostAsync(
            "/v1/notify/register-token",
            new { token = "fcm-token-1", platform = "android", deviceId = "install-1" },
            harness.Tokens.Passenger(user.Id));

        Assert.Equal(HttpStatusCode.NoContent, registered.StatusCode);

        using var response = await harness.SendInternalAsync(new
        {
            notificationType = NotificationCatalogue.DriverAssigned,
            recipients = new[] { user.Id },
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        await harness.DeliverAsync();

        Assert.Single(harness.Pushes.Sent);
    }

    /// <summary>
    /// FCM and APNs reissue a token to whichever install now owns it, so registering one another
    /// account holds <em>moves</em> it. Without that, a reinstall leaves a dead handle receiving
    /// somebody else's ride offers (<c>ux_notif_tokens_token</c>, C005 decision 8).
    /// </summary>
    [Fact]
    public async Task Registering_a_token_another_account_holds_moves_it()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var first = await harness.Seed.UserAsync();
        var second = await harness.Seed.UserAsync();

        using (var one = await harness.PostAsync(
                   "/v1/notify/register-token",
                   new { token = "shared-handle", platform = "android", deviceId = "install-1" },
                   harness.Tokens.Passenger(first.Id)))
        {
            Assert.Equal(HttpStatusCode.NoContent, one.StatusCode);
        }

        using (var two = await harness.PostAsync(
                   "/v1/notify/register-token",
                   new { token = "shared-handle", platform = "android", deviceId = "install-2" },
                   harness.Tokens.Passenger(second.Id)))
        {
            Assert.Equal(HttpStatusCode.NoContent, two.StatusCode);
        }

        await using var connection = await harness.OpenAsync();

        var owners = await connection.QueryAsync<Guid>(
            "SELECT user_id FROM comms.notification_tokens WHERE token = 'shared-handle';");

        Assert.Equal([second.Id], owners);
    }

    /// <summary>
    /// A reinstall on the same handset arrives with a new token and the same device id. The old
    /// handle is retired, or every offer fans out to a dead one (<c>ux_notif_tokens_device</c>, 1308).
    /// </summary>
    [Fact]
    public async Task A_reinstall_replaces_the_handle_the_install_used_to_hold()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var user = await harness.Seed.UserAsync();
        var bearer = harness.Tokens.Passenger(user.Id);

        foreach (var token in new[] { "handle-before", "handle-after" })
        {
            using var response = await harness.PostAsync(
                "/v1/notify/register-token",
                new { token, platform = "android", deviceId = "install-1" },
                bearer);

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        await using var connection = await harness.OpenAsync();

        var tokens = await connection.QueryAsync<string>(
            "SELECT token FROM comms.notification_tokens WHERE user_id = @Id;", new { Id = user.Id });

        Assert.Equal(["handle-after"], tokens);
    }

    [Fact]
    public async Task An_unknown_platform_is_refused()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var user = await harness.Seed.UserAsync();

        using var response = await harness.PostAsync(
            "/v1/notify/register-token",
            new { token = "fcm-token", platform = "windows-phone", deviceId = "install-1" },
            harness.Tokens.Passenger(user.Id));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var (code, _) = await NotificationHarness.ProblemAsync(response);
        Assert.Equal("validation-failed", code);
    }

    /// <summary>
    /// US-10.7's switch, end to end: a muted type is refused at enqueue and the row says why, so
    /// "I never got it" has an answer that is not "we do not know".
    /// </summary>
    [Fact]
    public async Task A_muted_type_is_suppressed_rather_than_lost()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var user = await harness.Seed.UserAsync();
        await harness.Seed.DeviceAsync(user.Id);

        using (var muted = await harness.SetPreferencesAsync(
                   new Dictionary<string, bool> { [NotificationCatalogue.LowBalance] = false },
                   harness.Tokens.Passenger(user.Id)))
        {
            var body = await NotificationHarness.OkAsync<PreferencesResponse>(muted, "PUT /v1/notify/preferences");
            Assert.False(body.Preferences[NotificationCatalogue.LowBalance]);
        }

        await harness.HandleAsync<WalletEventHandler>(user.Id.ToString(), "wallet.low_balance", new
        {
            ownerId = user.Id,
            balanceMinor = 15_000L,
            thresholdMinor = 20_000L,
            currency = "LKR",
            severity = "low",
            notificationType = "LOW_BALANCE",
            occurredAt = NotificationHarness.DefaultNow,
        });

        await harness.DeliverAsync();

        var row = Assert.Single(await harness.QueueAsync(user.Id));

        Assert.Equal(NotificationStatuses.Suppressed, row.Status);
        Assert.Empty(harness.Pushes.Sent);
    }

    /// <summary>
    /// <c>MageRideJson</c>'s camelCase dictionary-key policy would rewrite <c>LOW_BALANCE</c> as
    /// <c>lOW_BALANCE</c> once, silently, and the mute would stop matching the notification it was
    /// for. iam-svc solves it with a converter; this side writes the document by hand — and this is
    /// the test that keeps either from regressing.
    /// </summary>
    [Fact]
    public async Task A_preference_key_survives_the_round_trip_verbatim()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var user = await harness.Seed.UserAsync();

        using var response = await harness.SetPreferencesAsync(
            new Dictionary<string, bool>
            {
                [NotificationCatalogue.ScheduledReminder] = false,
                [NotificationCatalogue.LowBalance] = true,
            },
            harness.Tokens.Passenger(user.Id));

        var body = await NotificationHarness.OkAsync<PreferencesResponse>(response, "PUT /v1/notify/preferences");

        Assert.False(body.Preferences["SCHEDULED_REMINDER"]);
        Assert.True(body.Preferences["LOW_BALANCE"]);

        // And at rest, in iam-svc's column, spelled the same way.
        var stored = await harness.PreferencesAsync(user.Id);

        Assert.False(stored["SCHEDULED_REMINDER"]);
        Assert.True(stored["LOW_BALANCE"]);
    }

    /// <summary>
    /// A cancellation a passenger cannot be told about is a passenger left waiting for a car that is
    /// not coming. The write succeeds — the contract promises it — and the switch does not take.
    /// </summary>
    [Fact]
    public async Task A_safety_critical_switch_is_accepted_and_ignored()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var user = await harness.Seed.UserAsync();
        await harness.Seed.DeviceAsync(user.Id);

        using var response = await harness.SetPreferencesAsync(
            new Dictionary<string, bool> { [NotificationCatalogue.RideCancelled] = false },
            harness.Tokens.Passenger(user.Id));

        var body = await NotificationHarness.OkAsync<PreferencesResponse>(response, "PUT /v1/notify/preferences");

        // The response says what is in force, not what was asked for.
        Assert.True(body.Preferences[NotificationCatalogue.RideCancelled]);

        // Nothing was stored either, so iam-svc and this service cannot disagree about it.
        Assert.DoesNotContain(NotificationCatalogue.RideCancelled, await harness.PreferencesAsync(user.Id));

        var rideId = Guid.NewGuid();

        await harness.HandleAsync<RideEventHandler>(rideId.ToString(), "ride.cancelled", new
        {
            eventId = Guid.NewGuid(),
            eventType = "ride.cancelled",
            rideId,
            version = 3,
            ts = NotificationHarness.DefaultNow,
            payload = new { passengerId = user.Id, bookerId = user.Id, state = "CancelledByRider", kind = "passenger" },
        });

        await harness.DeliverAsync();

        Assert.Single(harness.Pushes.Sent);
    }

    /// <summary>
    /// An unknown type is refused loudly: storing it would grow the column with keys nothing
    /// resolves, and the client that sent it has a bug it should hear about.
    /// </summary>
    [Fact]
    public async Task An_unknown_notification_type_is_refused()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var user = await harness.Seed.UserAsync();

        using var response = await harness.SetPreferencesAsync(
            new Dictionary<string, bool> { ["NOT_A_TYPE"] = false },
            harness.Tokens.Passenger(user.Id));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var (code, _) = await NotificationHarness.ProblemAsync(response);
        Assert.Equal("validation-failed", code);
    }

    [Fact]
    public async Task The_bearer_routes_refuse_an_anonymous_caller()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        using var response = await harness.Client.PostAsync("/v1/notify/register-token", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// D-27's schedule, doubling from the base and settling at <c>Failed</c> after
    /// <c>MaxAttempts</c> — asserted on the row, because that is where the schedule survives a
    /// restart.
    /// </summary>
    [Fact]
    public async Task A_refused_push_backs_off_exponentially_and_then_fails()
    {
        await using var harness = await NotificationHarness.StartAsync(
            postgres,
            redis,
            new Dictionary<string, string?>
            {
                ["Notification:MaxAttempts"] = "3",
                ["Notification:BackoffBase"] = "00:00:05",
            });

        var user = await harness.Seed.UserAsync();
        await harness.Seed.DeviceAsync(user.Id);

        harness.Pushes.Refuse = true;

        using var accepted = await harness.SendInternalAsync(new
        {
            notificationType = NotificationCatalogue.DriverAssigned,
            recipients = new[] { user.Id },
        });

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        await harness.DeliverAsync();

        var row = Assert.Single(await harness.QueueAsync(user.Id));

        Assert.Equal(NotificationStatuses.Pending, row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.Equal(NotificationHarness.DefaultNow.AddSeconds(5), row.NextAttemptAt);

        // Not due yet: the lease and the backoff both hold it back.
        Assert.Equal(0, await harness.DeliverAsync());

        harness.Clock.Advance(TimeSpan.FromSeconds(5));
        await harness.DeliverAsync();

        row = Assert.Single(await harness.QueueAsync(user.Id));

        Assert.Equal(2, row.Attempts);
        Assert.Equal(harness.Clock.GetUtcNow().AddSeconds(10), row.NextAttemptAt);

        harness.Clock.Advance(TimeSpan.FromSeconds(10));
        await harness.DeliverAsync();

        row = Assert.Single(await harness.QueueAsync(user.Id));

        Assert.Equal(NotificationStatuses.Failed, row.Status);
        Assert.Equal(3, row.Attempts);
    }

    /// <summary>
    /// A provider that says a token is gone is not a transient failure: the handle is deleted, or
    /// every future offer fans out to it for ever.
    /// </summary>
    [Fact]
    public async Task A_dead_device_token_is_dropped_rather_than_retried()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var user = await harness.Seed.UserAsync();
        await harness.Seed.DeviceAsync(user.Id);

        harness.Pushes.TokensAreDead = true;

        using var accepted = await harness.SendInternalAsync(new
        {
            notificationType = NotificationCatalogue.DriverAssigned,
            recipients = new[] { user.Id },
        });

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        await harness.DeliverAsync();

        Assert.Equal(NotificationStatuses.Failed, (await harness.QueueAsync(user.Id)).Single().Status);

        await using var connection = await harness.OpenAsync();

        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "SELECT count(*)::int FROM comms.notification_tokens WHERE user_id = @Id;", new { Id = user.Id }));
    }

    /// <summary>
    /// The retention sweep is what takes an unregistered recipient's number back out of the
    /// database (E-06); a settled row older than the window goes, an unsent one stays.
    /// </summary>
    [Fact]
    public async Task Retention_removes_settled_rows_and_leaves_pending_ones()
    {
        await using var harness = await NotificationHarness.StartAsync(
            postgres, redis, new Dictionary<string, string?> { ["Notification:Retention"] = "1.00:00:00" });

        var user = await harness.Seed.UserAsync();
        await harness.Seed.DeviceAsync(user.Id);

        using (var sent = await harness.SendInternalAsync(new
               {
                   notificationType = NotificationCatalogue.DriverAssigned,
                   recipients = new[] { user.Id },
               }))
        {
            Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        }

        await harness.DeliverAsync();

        // A second one that never leaves.
        harness.Pushes.Refuse = true;

        using (var pending = await harness.SendInternalAsync(new
               {
                   notificationType = NotificationCatalogue.DriverArrived,
                   recipients = new[] { user.Id },
               }))
        {
            Assert.Equal(HttpStatusCode.Accepted, pending.StatusCode);
        }

        await harness.DeliverAsync();

        // **Aged against the harness clock, because that is the clock the cut-off comes from.**
        // `RetentionWorker.SweepAsync` purges everything before `clock.GetUtcNow() - Retention`, and
        // `clock` is the `FakeTimeProvider` this harness registers as the `TimeProvider` singleton —
        // not the database's. `created_at` defaults to `now()` at insert, so the two agree in
        // production and diverge here by however far the fake instant sits from today.
        //
        // Written as `now() - interval '2 days'` this test passed on the day it was written and
        // failed silently thereafter: with the fake clock pinned and `now()` advancing, a row aged
        // two days behind the *database* eventually lands after a cut-off derived from a *fixed*
        // date, and the sweep correctly purged nothing. Pinning the value to the same clock the
        // production code reads makes the assertion independent of the wall clock for good.
        await using (var connection = await harness.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE comms.notifications SET created_at = @CreatedAt;",
                new { CreatedAt = harness.Clock.GetUtcNow() - TimeSpan.FromDays(2) });
        }

        Assert.Equal(1, await harness.Resolve<MageRide.Notification.Sending.RetentionWorker>()
            .SweepAsync(CancellationToken.None));

        var remaining = Assert.Single(await harness.QueueAsync(user.Id));

        Assert.Equal(NotificationStatuses.Pending, remaining.Status);
    }
}
