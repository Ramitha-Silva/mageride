using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Dapper;
using MageRide.Ride.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Ride.Tests.Integration;

/// <summary>
/// DoD item 4: "ride.* events reach Redpanda through the outbox, never by direct publish"
/// (D6' §2.4, R-13, E-09).
/// </summary>
/// <remarks>
/// A capturing publisher would prove the dispatcher was called; it would not prove anything was
/// produced, and it could not tell a direct publish from an outbox drain. So this runs against a
/// real broker and asserts the property that distinguishes the two: every message carries the
/// <c>outboxId</c> header the dispatcher stamps, and the row it came from is marked dispatched.
/// </remarks>
[Collection<RideCollection>]
public sealed class OutboxPipelineTests(PostgresFixture postgres, RedpandaFixture redpanda)
{
    private const string Topic = "ride.events";

    [Fact]
    public async Task Every_ride_event_reaches_redpanda_through_the_outbox_in_order()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await redpanda.CreateTopicAsync(Topic);

        await using var harness = await RideHarness.StartAsync(postgres, new Dictionary<string, string?>
        {
            ["Kafka:BootstrapServers"] = redpanda.BootstrapServers,
            ["Outbox:DispatcherEnabled"] = "true",
            // LISTEN/NOTIFY is the trigger under test; a short poll would mask a broken listener.
            ["Outbox:PollInterval"] = "00:01:00",
        });

        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());
        var driver = await harness.CreateDriverAsync();

        var rideId = (await harness.RequestRideAsync(passenger)).GetProperty("rideId").GetGuid();
        var offer = await harness.OfferAsync(rideId, driver, ttlSeconds: 60);

        var accepted = await harness.PostAsync(
            $"/v1/rides/{rideId}/offer/{driver.DriverId}/accept",
            new { offerId = offer.OfferId.ToString(), version = offer.Version },
            driver.Bearer);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var version = (await RideHarness.ReadJsonAsync(accepted)).GetProperty("version").GetInt64();
        version = await AdvanceAsync(harness, rideId, "arrive", driver.Bearer, version);
        version = await AdvanceAsync(harness, rideId, "start", driver.Bearer, version);
        await AdvanceAsync(harness, rideId, "complete", driver.Bearer, version);

        var delivered = await ConsumeAsync(rideId, expected: 6, TimeSpan.FromSeconds(45));

        Assert.Equal(
            ["ride.requested", "offer.created", "ride.accepted", "ride.driver_arrived", "ride.started", "ride.completed"],
            delivered.Select(m => m.EventType).ToArray());

        // Per-aggregate ordering is the guarantee consumers rely on (D6' §2.3), and it comes from
        // the rideId partition key.
        Assert.All(delivered, m => Assert.Equal(rideId.ToString(), m.Key));

        // The envelope is D6' §2.2's, not an ad-hoc shape.
        var last = delivered[^1];
        Assert.Equal(rideId, last.Envelope.GetProperty("rideId").GetGuid());
        Assert.Equal("ride.completed", last.Envelope.GetProperty("eventType").GetString());
        Assert.NotEqual(Guid.Empty, last.Envelope.GetProperty("eventId").GetGuid());
        Assert.Equal("PaymentPending", last.Envelope.GetProperty("payload").GetProperty("state").GetString());
        Assert.Equal(driver.DriverId, last.Envelope.GetProperty("payload").GetProperty("driverId").GetGuid());
        Assert.Equal(
            RideHarness.Pickup.Latitude,
            last.Envelope.GetProperty("payload").GetProperty("pickup").GetProperty("lat").GetDouble(),
            6);

        // The version on the envelope is the one a consumer will find if it reads the row back.
        await using var connection = await harness.OpenAsync();
        Assert.Equal(
            await connection.ExecuteScalarAsync<long>(
                "SELECT version FROM rides.rides WHERE id = @RideId;", new { RideId = rideId }),
            last.Envelope.GetProperty("version").GetInt64());

        // Nothing was published outside the outbox: every delivered message names the row it came
        // from, and every row for this ride is marked dispatched.
        var outboxIds = (await connection.QueryAsync<long>(
            "SELECT id FROM rides.outbox WHERE aggregate_id = @RideId ORDER BY id;", new { RideId = rideId })).ToArray();

        Assert.Equal(outboxIds, delivered.Select(m => m.OutboxId).ToArray());

        // POLLED, because arriving on the topic does not mean the row has been stamped yet.
        // The dispatcher produces first and marks `dispatched_at` after the delivery report — it
        // must, or a crash between the two would lose an event the broker never took — so between
        // this test consuming the message and the UPDATE committing there is a window of
        // milliseconds in which the column is legitimately still NULL. `TripState.Api.Tests`'
        // twin was fixed for this on 2026-08-15; provisioning-svc's landed inside the same window on
        // 2026-08-20 ("Assert.NotNull() Failure: Value of type 'Nullable<DateTimeOffset>' does
        // not have a value"); this twin carries the same race and has simply not lost the window
        // yet.
        //
        // Counted rather than read here, because this test consumes every row for the ride, so the
        // window is open on whichever of them was produced last.
        //
        // The assertion is unchanged in substance: a dispatcher that never stamps a row still
        // fails, five seconds later and with a message that says how many stayed NULL.
        int? unstamped = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            unstamped = await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM rides.outbox WHERE aggregate_id = @RideId AND dispatched_at IS NULL;",
                new { RideId = rideId });

            if (unstamped == 0)
            {
                break;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.True(
            unstamped == 0,
            $"{unstamped} rides.outbox row(s) for ride {rideId} reached Redpanda but were never "
            + "marked dispatched. The drain produces and then stamps; a row that stays NULL is a "
            + "drain that published and did not record it, which redelivers on the next sweep.");
    }

    /// <summary>
    /// R-13: an offer that failed to be placed publishes nothing. The transaction rolls back
    /// whole, so the <c>offer.created</c> row never exists and no driver is pushed a phantom offer.
    /// </summary>
    [Fact]
    public async Task A_rejected_offer_leaves_no_event_behind()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());
        var driver = await harness.CreateDriverAsync();

        var rideId = (await harness.RequestRideAsync(passenger)).GetProperty("rideId").GetGuid();

        // Straight to Offered without the Matching move: the conditional UPDATE finds no row.
        var rejected = await harness.PostInternalAsync(
            $"/v1/internal/rides/{rideId}/offer",
            new
            {
                offerId = Guid.NewGuid().ToString(),
                driverId = driver.DriverId.ToString(),
                vehicleId = driver.VehicleId.ToString(),
            });

        await ProblemDocument.AssertAsync(rejected, HttpStatusCode.BadRequest, "illegal-transition");

        await using var connection = await harness.OpenAsync();

        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM rides.outbox WHERE aggregate_id = @RideId AND event_type = 'offer.created';",
            new { RideId = rideId }));
        Assert.Equal("Requested", await connection.ExecuteScalarAsync<string>(
            "SELECT state FROM rides.rides WHERE id = @RideId;", new { RideId = rideId }));
    }

    private static async Task<long> AdvanceAsync(
        RideHarness harness, Guid rideId, string command, string bearer, long version)
    {
        var response = await harness.PostAsync($"/v1/rides/{rideId}/{command}", new { version }, bearer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await RideHarness.ReadJsonAsync(response)).GetProperty("version").GetInt64();
    }

    /// <summary>Reads the topic until this ride's events have all arrived, or the deadline passes.</summary>
    private async Task<IReadOnlyList<DeliveredEvent>> ConsumeAsync(Guid rideId, int expected, TimeSpan timeout)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = redpanda.BootstrapServers,
            GroupId = $"c022-outbox-{Guid.NewGuid()}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        };

        using var consumer = new ConsumerBuilder<string, byte[]>(config).Build();
        consumer.Subscribe(Topic);

        var delivered = new List<DeliveredEvent>(expected);
        var clock = Stopwatch.StartNew();

        try
        {
            while (delivered.Count < expected && clock.Elapsed < timeout)
            {
                var result = consumer.Consume(TimeSpan.FromSeconds(1));
                if (result?.Message is null)
                {
                    continue;
                }

                // The topic is shared with every other harness in this collection.
                if (!string.Equals(result.Message.Key, rideId.ToString(), StringComparison.Ordinal))
                {
                    continue;
                }

                var headers = result.Message.Headers.ToDictionary(
                    h => h.Key,
                    h => Encoding.UTF8.GetString(h.GetValueBytes()),
                    StringComparer.Ordinal);

                using var document = JsonDocument.Parse(result.Message.Value);

                delivered.Add(new DeliveredEvent(
                    Key: result.Message.Key,
                    EventType: headers["eventType"],
                    OutboxId: long.Parse(headers["outboxId"], System.Globalization.CultureInfo.InvariantCulture),
                    Envelope: document.RootElement.Clone()));
            }
        }
        finally
        {
            consumer.Close();
        }

        Assert.True(
            delivered.Count == expected,
            $"Expected {expected} ride.events for {rideId} within {timeout}; saw {delivered.Count} " +
            $"({string.Join(", ", delivered.Select(d => d.EventType))}).");

        await Task.CompletedTask;
        return delivered;
    }

    private sealed record DeliveredEvent(string Key, string EventType, long OutboxId, JsonElement Envelope);
}
