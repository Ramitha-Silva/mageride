using System.Net;
using Dapper;
using MageRide.E2E.Infrastructure;
using MageRide.Shared.Primitives;
using MageRide.TestKit;

namespace MageRide.E2E.Scenarios;

/// <summary>
/// <b>Package delivery — the two OTP gates, the lockout and the photograph (P-06, P-07, P-10).</b>
/// </summary>
/// <remarks>
/// <para>
/// A parcel travels the <em>same</em> Mode C machine a passenger does — ADD Appendix B.2 invariant 6
/// is literal and adds no states — so what these scenarios exercise is the pair of gates that decide
/// <em>whether</em> it may move: the pickup code at the sender's door and the delivery code, or a
/// photograph, at the recipient's.
/// </para>
/// <para>
/// <b>The delivery code is never read from the database</b>, because the server does not keep it: a
/// digest exists from booking and the plaintext that is actually sent is minted at the pickup and
/// leaves on <c>package.picked_up</c>. So each scenario learns it where its recipient learns it — a
/// registered one from the push notification-svc queued, an unregistered one from the SMS link and
/// the page it opens.
/// </para>
/// <para>
/// ADD §11.16, D5' §11, AL-21, AL-33, US-20.1–20.13.
/// </para>
/// </remarks>
[Collection<ProxyPackageCollection>]
[Trait("Category", "ProxyPackage")]
public sealed class PackageDeliveryScenario(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
    : ProxyPackageScenario(postgres, redis, redpanda)
{
    /// <summary>
    /// Sender's code at the door, recipient's code at the other end, and a parcel that is delivered.
    /// </summary>
    [Fact]
    public Task A_parcel_is_gated_by_the_pickup_code_and_released_by_the_delivery_code() =>
        RunAsync(async (fleet, rides) =>
        {
            // Registered, so the delivery code travels by push and this scenario is about the OTPs
            // rather than about AL-21's SMS branch — which the next test is.
            var recipient = await fleet.CreatePassengerAsync("Kamala");
            var ride = await AcceptedPackageAsync(fleet, rides, recipient.Phone);

            // ---- the gate ----------------------------------------------------------------------
            // `Accepted` is where a parcel waits, and `start` is not the door out of it: the pickup
            // OTP takes the same `Accepted|DriverArrived → InProgress` edge, which is the whole of
            // "one machine, three kinds".
            Assert.Equal("Accepted", (await fleet.ReadRideAsync(ride.RideId)).State);

            using (var wrong = await fleet.PickupOtpAsync(ride, NextWrongOtp(ride.PickupOtp!)))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
                Assert.Equal("invalid-otp", await ProxyPackageFleet.ProblemCodeAsync(wrong));
            }

            // A wrong code does not move the parcel, and the driver may still try again.
            Assert.Equal("Accepted", (await fleet.ReadRideAsync(ride.RideId)).State);

            using (var picked = await fleet.PickupOtpAsync(ride, ride.PickupOtp))
            {
                Assert.Equal(HttpStatusCode.OK, picked.StatusCode);

                var body = await ProxyPackageFleet.ReadJsonAsync(picked);
                Assert.Equal("InProgress", body.GetProperty("state").GetString());

                ride = ride with { Version = body.GetProperty("version").GetInt64() };
            }

            // **Both events at the gate.** `package.picked_up` co-fires with `ride.started`, because
            // dispatch-svc and everything else keyed on the lifecycle would otherwise never hear
            // that a driver had begun.
            var events = await fleet.ReadEventsAsync(ride.RideId);
            Assert.Contains("ride.started", events);
            Assert.Contains("package.picked_up", events);

            // ---- the recipient's code ----------------------------------------------------------
            // Minted at the pickup and carried on that one event: the only hop it exists in the
            // clear, which is where notification-svc reads it in production and where this reads it
            // too.
            var handover = await fleet.ReadEventPayloadAsync(ride.RideId, "package.picked_up");
            var deliveryOtp = handover.GetProperty("payload").GetProperty("deliveryOtp").GetString();

            Assert.NotNull(deliveryOtp);
            Assert.Equal(4, deliveryOtp!.Length);
            Assert.NotEqual(ride.PickupOtp, deliveryOtp);

            // The registered recipient is pushed to, not SMSed — AL-21's other branch. The code
            // itself rides in the push payload, which is why the SMS deliberately never carries one.
            await AwaitNotificationAsync(fleet, recipient.Id, "package_picked_up");
            Assert.True(
                await fleet.Sms.NothingSentToAsync(recipient.Phone, TimeSpan.FromSeconds(2)),
                "A recipient with an account was SMSed a tracking link they did not need (AL-21).");

            // ---- delivery ----------------------------------------------------------------------
            using (var wrong = await fleet.DeliveryOtpAsync(ride, NextWrongOtp(deliveryOtp)))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
                Assert.Equal("invalid-otp", await ProxyPackageFleet.ProblemCodeAsync(wrong));
            }

            Assert.Equal("InProgress", (await fleet.ReadRideAsync(ride.RideId)).State);

            using (var delivered = await fleet.DeliveryOtpAsync(ride, deliveryOtp))
            {
                Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
            }

            // `Completed` is transient here exactly as it is for a passenger ride: one transaction
            // carries the parcel through it to `PaymentPending`.
            Assert.Equal("PaymentPending", (await fleet.ReadRideAsync(ride.RideId)).State);

            var afterDelivery = await fleet.ReadEventsAsync(ride.RideId);
            Assert.Contains("ride.completed", afterDelivery);
            Assert.Contains("package.delivered", afterDelivery);

            // No photograph was needed, so none exists: P-10 is the alternative to the code, not a
            // companion to it.
            Assert.Empty(await fleet.ReadProofArtifactsAsync(ride.RideId));
        });

    /// <summary>
    /// <b>AL-21's other branch: a recipient with no app is SMSed a link to SCR-WT-002.</b>
    /// </summary>
    /// <remarks>
    /// The C122 fence names this as one of the two no-app paths. Everything is driven from the
    /// message: notification-svc mints a <c>package_recipient</c> token on <c>package.picked_up</c>,
    /// writes the delivery code to Redis in the same branch — because the SMS deliberately does not
    /// carry four digits, and D6' I-23.3 has the page show them instead — and this reads both the
    /// way the recipient does.
    /// </remarks>
    [Fact]
    public Task An_unregistered_recipient_is_SMSed_a_link_and_reads_their_code_off_the_page() =>
        RunAsync(async (fleet, rides) =>
        {
            var recipientPhone = ProxyPackageFleet.UnregisteredPhone();
            var ride = await AcceptedPackageAsync(fleet, rides, recipientPhone);

            // Nothing has been sent yet: AL-21's branch fires on the pickup, not on the booking.
            Assert.True(
                await fleet.Sms.NothingSentToAsync(recipientPhone, TimeSpan.FromSeconds(1)),
                "The recipient was told about a parcel that was still on the sender's doorstep.");

            using (var picked = await fleet.PickupOtpAsync(ride, ride.PickupOtp))
            {
                Assert.Equal(HttpStatusCode.OK, picked.StatusCode);
            }

            // ---- the SMS -----------------------------------------------------------------------
            var message = await fleet.Sms.AwaitSmsAsync(recipientPhone);
            var token = SmsGateway.TokenIn(message);

            var minted = Assert.Single(await fleet.ReadShareTokensAsync(ride.RideId, null));
            Assert.Equal("package_recipient", minted.Scope);
            Assert.Equal(token, minted.Token);

            // **The code is not in the message, and that is the decision rather than an omission.**
            // ADD §11.16 hands the delivery OTP to the recipient at pickup and D6' I-23.3 has the
            // page show it "post token validation", so an SMS carrying four digits would put a live
            // credential in a message that is forwarded, screenshotted and left on a lock screen.
            var expected = await fleet.ReadEventPayloadAsync(ride.RideId, "package.picked_up");
            var deliveryOtp = expected.GetProperty("payload").GetProperty("deliveryOtp").GetString()!;

            Assert.DoesNotContain(deliveryOtp, message.Body, StringComparison.Ordinal);

            // ---- SCR-WT-002 --------------------------------------------------------------------
            var page = await fleet.Web.OpenAsync(token);

            Assert.Equal(HttpStatusCode.OK, page.Status);
            Assert.Equal("package", page.Json.GetProperty("kind").GetString());

            // The four digits, to the holder of this token and nobody else — the unregistered
            // recipient was the one party on the platform with no way to learn their own code.
            Assert.Equal(deliveryOtp, page.Json.GetProperty("deliveryOtp").GetString());

            // The parcel is aboard and the page says so. `PickedUp` rather than `InTransit` because
            // no position has been published in this fleet — claiming a parcel is on its way with
            // nothing to show for it would be a guess told as a fact.
            Assert.Equal("PickedUp", page.Json.GetProperty("status").GetString());

            // AL-48: the driver's real number, as a `tel:` link, with no masking anywhere near it.
            var driverCard = page.Json.GetProperty("driver");
            Assert.Equal(ride.Driver.Phone, driverCard.GetProperty("phone").GetString());
            Assert.Equal(ride.Driver.Plate, driverCard.GetProperty("regNo").GetString());

            // P-09: the sender is a display name and never a number.
            Assert.False(page.Mentions(ride.Passenger.Phone), "SCR-WT-002 showed the sender's number (P-09).");

            // ---- and the code the page showed is the one that opens the door --------------------
            using (var delivered = await fleet.DeliveryOtpAsync(
                ride, page.Json.GetProperty("deliveryOtp").GetString()))
            {
                Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
            }

            Assert.Equal("PaymentPending", (await fleet.ReadRideAsync(ride.RideId)).State);
        });

    /// <summary>
    /// Five wrong codes and the parcel is locked to the admin queue (P-07).
    /// </summary>
    /// <remarks>
    /// <b>A correct code never spends an attempt and neither does a malformed one.</b> The gate is
    /// two guarded <c>UPDATE</c>s — one that matches the digest and moves the ride, one that charges
    /// the budget and applies only when the digest does not — so this counts exactly what a wrong
    /// guess costs. The attempt that <em>exhausts</em> the budget raises <c>package.otp_locked</c>;
    /// the next one is <c>423</c>.
    /// </remarks>
    [Fact]
    public Task Five_wrong_delivery_codes_lock_the_parcel_to_the_admin_queue() =>
        RunAsync(async (fleet, rides) =>
        {
            var ride = await AcceptedPackageAsync(fleet, rides, ProxyPackageFleet.UnregisteredPhone());

            // A malformed code is refused on shape and buys the attacker nothing — otherwise five
            // taps of an empty field would lock out a driver who had not guessed at all.
            using (var malformed = await fleet.PickupOtpAsync(ride, "abc"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
            }

            Assert.Equal(0, await ReadAttemptsAsync(fleet, ride.RideId, "pickup"));

            using (var picked = await fleet.PickupOtpAsync(ride, ride.PickupOtp))
            {
                Assert.Equal(HttpStatusCode.OK, picked.StatusCode);
            }

            // A correct code spends nothing either.
            Assert.Equal(0, await ReadAttemptsAsync(fleet, ride.RideId, "pickup"));

            var handover = await fleet.ReadEventPayloadAsync(ride.RideId, "package.picked_up");
            var deliveryOtp = handover.GetProperty("payload").GetProperty("deliveryOtp").GetString()!;
            var wrong = NextWrongOtp(deliveryOtp);

            // Four guesses inside the budget.
            for (var attempt = 1; attempt <= 4; attempt++)
            {
                using var refused = await fleet.DeliveryOtpAsync(ride, wrong);

                Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
                Assert.Equal(attempt, await ReadAttemptsAsync(fleet, ride.RideId, "delivery"));
            }

            // The fifth exhausts it. Still `invalid-otp` — the guess was wrong, which is what the
            // driver is told — and it is this one that raises the event the admin queue reads.
            using (var exhausting = await fleet.DeliveryOtpAsync(ride, wrong))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, exhausting.StatusCode);
            }

            Assert.Equal(5, await ReadAttemptsAsync(fleet, ride.RideId, "delivery"));
            Assert.Contains("package.otp_locked", await fleet.ReadEventsAsync(ride.RideId));

            // The sixth is refused without being counted: there is no budget left to charge.
            using (var locked = await fleet.DeliveryOtpAsync(ride, wrong))
            {
                Assert.Equal(HttpStatusCode.Locked, locked.StatusCode);
            }

            // And the *correct* code is refused too, which is the point of a lockout rather than a
            // rate limit — the parcel now needs a human.
            using (var correct = await fleet.DeliveryOtpAsync(ride, deliveryOtp))
            {
                Assert.Equal(HttpStatusCode.Locked, correct.StatusCode);
            }

            Assert.Equal("InProgress", (await fleet.ReadRideAsync(ride.RideId)).State);

            // ---- and the way out is the photograph ---------------------------------------------
            // P-10 is what a driver holding a locked parcel actually does. The lockout gates the
            // code, not the delivery — the alternative would be a parcel nobody can hand over.
            using (var photographed = await fleet.ProofPhotoAsync(ride, ride.Dropoff))
            {
                Assert.Equal(HttpStatusCode.Created, photographed.StatusCode);
            }

            Assert.Equal("PaymentPending", (await fleet.ReadRideAsync(ride.RideId)).State);
        });

    /// <summary>
    /// Nobody answers the door: the driver photographs the handover instead (P-10).
    /// </summary>
    [Fact]
    public Task A_photographed_handover_completes_a_delivery_the_recipient_did_not_answer() =>
        RunAsync(async (fleet, rides) =>
        {
            var ride = await AcceptedPackageAsync(fleet, rides, ProxyPackageFleet.UnregisteredPhone());

            using (var picked = await fleet.PickupOtpAsync(ride, ride.PickupOtp))
            {
                Assert.Equal(HttpStatusCode.OK, picked.StatusCode);
            }

            var doorstep = new GeoPoint(ride.Dropoff.Latitude, ride.Dropoff.Longitude);

            using (var photographed = await fleet.ProofPhotoAsync(ride, doorstep))
            {
                Assert.Equal(HttpStatusCode.Created, photographed.StatusCode);

                var body = await ProxyPackageFleet.ReadJsonAsync(photographed);

                // **The route completes the delivery rather than merely filing the picture.** ADD
                // §11.16 draws the photograph and the delivery OTP as alternatives into `Completed`;
                // a route that only stored the file would leave the parcel delivered and the ride
                // running.
                Assert.Equal("PaymentPending", body.GetProperty("state").GetString());
                Assert.NotEqual(Guid.Empty, body.GetProperty("artifactId").GetGuid());
            }

            var artefact = Assert.Single(await fleet.ReadProofArtifactsAsync(ride.RideId));

            Assert.Equal("delivery_photo", artefact.Kind);

            // D-36: `Storage__S3__Endpoint` is unset in this fleet, so the kernel's object store
            // falls back to the filesystem and says so in the pointer. The bytes really crossed an
            // HTTP boundary and really landed somewhere.
            Assert.StartsWith("file://", artefact.StorageUrl, StringComparison.Ordinal);

            // The tamper evidence a dispute needs, and the position the photograph was taken at.
            await using var connection = await fleet.OpenAsync();

            var stored = await connection.QuerySingleAsync<(byte[] Sha256, double? Lat)>(
                """
                SELECT sha256, ST_Y(captured_geo::geometry)
                  FROM rides.proof_artifacts WHERE ride_id = @RideId;
                """,
                new { RideId = ride.RideId });

            Assert.Equal(32, stored.Sha256.Length);
            Assert.Equal(doorstep.Latitude, stored.Lat!.Value, precision: 4);

            Assert.Contains("package.delivered", await fleet.ReadEventsAsync(ride.RideId));
        });

    // ---------------------------------------------------------------------------------------------

    /// <summary>Four digits that are not <paramref name="right"/>, and are still well formed.</summary>
    private static string NextWrongOtp(string right) =>
        right == "0000" ? "1111" : "0000";

    private static async Task<int> ReadAttemptsAsync(ProxyPackageFleet fleet, Guid rideId, string purpose)
    {
        await using var connection = await fleet.OpenAsync();

        return await connection.ExecuteScalarAsync<int>(
            $"SELECT {purpose}_otp_attempts FROM rides.rides WHERE id = @RideId;",
            new { RideId = rideId });
    }

    /// <inheritdoc cref="ProxyBookingScenario"/>
    private static async Task AwaitNotificationAsync(
        ProxyPackageFleet fleet, Guid userId, string type, TimeSpan? within = null)
    {
        var deadline = DateTimeOffset.UtcNow + (within ?? TimeSpan.FromSeconds(45));

        do
        {
            await using (var connection = await fleet.OpenAsync())
            {
                var queued = await connection.ExecuteScalarAsync<int>(
                    """
                    SELECT count(*)::int FROM comms.notifications
                     WHERE recipient_user_id = @UserId AND notification_type = @Type;
                    """,
                    new { UserId = userId, Type = type });

                if (queued > 0)
                {
                    return;
                }
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.Fail($"notification-svc never queued a '{type}' for {userId}.");
    }
}
