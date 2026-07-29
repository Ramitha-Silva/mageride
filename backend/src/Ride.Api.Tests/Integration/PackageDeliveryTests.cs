using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using MageRide.Ride.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Ride.Tests.Integration;

/// <summary>
/// DoD items 3 and 4: "a sixth wrong pickup OTP returns 423 and raises an admin-queue item", and
/// "a package delivered by proof photo (recipient absent) reaches Completed with the artifact
/// persisted" (P-06, P-07, P-10, AL-21, AL-33).
/// </summary>
[Collection<RideCollection>]
public sealed class PackageDeliveryTests(PostgresFixture postgres)
{
    /// <summary>
    /// The whole delivery, gate for gate. The pickup OTP replaces <c>start</c> and the delivery OTP
    /// replaces <c>complete</c> — the same eighteen states, two different doors (ADD §11.16).
    /// </summary>
    [Fact]
    public async Task A_package_walks_the_machine_through_its_two_otp_gates()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var sender = await harness.CreateUserAsync();
        var recipient = IamLookupStub.UnregisteredPhone();
        var driver = await harness.CreateDriverAsync("motorbike");

        var response = await harness.RequestPackageRideAsync(sender.Bearer, recipient);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var booked = await RideHarness.ReadJsonAsync(response);
        var rideId = booked.GetProperty("rideId").GetGuid();
        var pickupOtp = booked.GetProperty("pickupOtp").GetString();

        // P-07: the sender is shown four digits, exactly once.
        Assert.NotNull(pickupOtp);
        Assert.Equal(4, pickupOtp!.Length);
        Assert.All(pickupOtp, character => Assert.True(char.IsAsciiDigit(character)));

        // Neither code is at rest in the clear.
        await using (var connection = await harness.OpenAsync())
        {
            var hashes = await connection.QuerySingleAsync<(byte[] Pickup, byte[] Delivery, string Size, string? Recipient)>(
                """
                SELECT pickup_otp_hash, delivery_otp_hash, package_size, recipient_phone
                  FROM rides.rides WHERE id = @RideId;
                """,
                new { RideId = rideId });

            Assert.Equal(32, hashes.Pickup.Length);
            Assert.Equal(32, hashes.Delivery.Length);
            Assert.NotEqual(hashes.Pickup, hashes.Delivery);
            Assert.Equal("S", hashes.Size);
            Assert.Equal(recipient, hashes.Recipient);
        }

        // The size travels on `ride.requested`, which is dispatch-svc's only message and its P-11
        // gate's only input.
        var requested = (await harness.ReadEventPayloadAsync(rideId, "ride.requested")).GetProperty("payload");
        Assert.Equal("package", requested.GetProperty("kind").GetString());
        Assert.Equal("S", requested.GetProperty("packageSize").GetString());
        Assert.Equal("One box of mangoes", requested.GetProperty("packageDescription").GetString());

        var offer = await harness.OfferAsync(rideId, driver);

        var accepted = await harness.PostAsync(
            $"/v1/rides/{rideId}/offer/{driver.DriverId}/accept",
            new { offerId = offer.OfferId.ToString(), version = offer.Version },
            driver.Bearer);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        // ---- the pickup gate ---------------------------------------------------------------
        var picked = await harness.PostAsync(
            $"/v1/rides/{rideId}/package/pickup-otp", new { otp = pickupOtp }, driver.Bearer);

        Assert.Equal(HttpStatusCode.OK, picked.StatusCode);
        Assert.Equal("InProgress", (await RideHarness.ReadJsonAsync(picked)).GetProperty("state").GetString());

        // Two events: the aggregate's own state snapshot, and ADD §11.16's domain event carrying
        // the recipient's code and the AL-21 branch input.
        var handover = (await harness.ReadEventPayloadAsync(rideId, "package.picked_up")).GetProperty("payload");
        var deliveryOtp = handover.GetProperty("deliveryOtp").GetString();

        Assert.Equal(4, deliveryOtp!.Length);
        Assert.Equal(recipient, handover.GetProperty("recipientPhone").GetString());
        Assert.Equal("Kamala", handover.GetProperty("recipientName").GetString());
        Assert.Contains("ride.started", await harness.ReadEventsAsync(rideId));

        // The pickup code does not open the second gate.
        var wrongGate = await harness.PostAsync(
            $"/v1/rides/{rideId}/package/delivery-otp", new { otp = pickupOtp }, driver.Bearer);

        await ProblemDocument.AssertAsync(wrongGate, HttpStatusCode.Unauthorized, "invalid-otp");

        // ---- the delivery gate -------------------------------------------------------------
        var delivered = await harness.PostAsync(
            $"/v1/rides/{rideId}/package/delivery-otp", new { otp = deliveryOtp }, driver.Bearer);

        Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
        Assert.Equal("PaymentPending", (await RideHarness.ReadJsonAsync(delivered)).GetProperty("state").GetString());

        var events = await harness.ReadEventsAsync(rideId);
        Assert.Contains("package.delivered", events);

        // dispatch-svc releases the driver on `ride.completed`; a package that only said
        // `package.delivered` would leave them ghost-busy.
        Assert.Contains("ride.completed", events);

        // `package.delivered` never carries a code — by then it has been spent.
        var done = (await harness.ReadEventPayloadAsync(rideId, "package.delivered")).GetProperty("payload");
        Assert.False(done.TryGetProperty("deliveryOtp", out _));
        Assert.Equal("Delivered", done.GetProperty("packageStatus").GetString());
    }

    /// <summary>
    /// DoD item 3. Five wrong codes exhaust the budget and raise the admin-queue item; the sixth is
    /// answered <c>423 otp-locked</c> and the delivery needs a human (P-07).
    /// </summary>
    [Fact]
    public async Task A_sixth_wrong_pickup_code_is_locked_and_the_admin_queue_has_been_raised()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var package = await harness.DrivePackageToAsync("Accepted");

        // Five wrong codes, none of them the real one.
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var wrong = WrongCode(package.PickupOtp, attempt);

            var response = await harness.PostAsync(
                $"/v1/rides/{package.RideId}/package/pickup-otp", new { otp = wrong }, package.Driver.Bearer);

            await ProblemDocument.AssertAsync(response, HttpStatusCode.Unauthorized, "invalid-otp");
        }

        await using (var connection = await harness.OpenAsync())
        {
            Assert.Equal(
                5,
                await connection.ExecuteScalarAsync<short>(
                    "SELECT pickup_otp_attempts FROM rides.rides WHERE id = @RideId;",
                    new { RideId = package.RideId }));
        }

        // The queue item is raised by the attempt that spent the budget, not by the one after it:
        // the delivery is stuck the moment the last try is used.
        var locked = (await harness.ReadEventPayloadAsync(package.RideId, "package.otp_locked"))
            .GetProperty("payload");

        Assert.Equal("pickup", locked.GetProperty("gate").GetString());
        Assert.Equal(5, locked.GetProperty("attempts").GetInt32());

        // The sixth.
        var sixth = await harness.PostAsync(
            $"/v1/rides/{package.RideId}/package/pickup-otp",
            new { otp = WrongCode(package.PickupOtp, 6) },
            package.Driver.Bearer);

        await ProblemDocument.AssertAsync(sixth, HttpStatusCode.Locked, "otp-locked");

        // Even the *correct* code no longer opens it — the budget is the control, and it is spent.
        var correct = await harness.PostAsync(
            $"/v1/rides/{package.RideId}/package/pickup-otp",
            new { otp = package.PickupOtp },
            package.Driver.Bearer);

        await ProblemDocument.AssertAsync(correct, HttpStatusCode.Locked, "otp-locked");

        var (state, _) = await harness.ReadRideAsync(package.RideId);
        Assert.Equal("Accepted", state);

        // Exactly one queue item, however many attempts followed.
        var events = await harness.ReadEventsAsync(package.RideId);
        Assert.Single(events, name => name == "package.otp_locked");
    }

    /// <summary>A malformed code is refused without spending an attempt (it is not a guess).</summary>
    [Fact]
    public async Task A_malformed_code_does_not_cost_an_attempt()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var package = await harness.DrivePackageToAsync("Accepted");

        foreach (var malformed in new[] { "12", "12345", "abcd", "" })
        {
            var response = await harness.PostAsync(
                $"/v1/rides/{package.RideId}/package/pickup-otp", new { otp = malformed }, package.Driver.Bearer);

            await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
        }

        await using var connection = await harness.OpenAsync();
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<short>(
                "SELECT pickup_otp_attempts FROM rides.rides WHERE id = @RideId;", new { RideId = package.RideId }));

        // …and the real code still works.
        var picked = await harness.PostAsync(
            $"/v1/rides/{package.RideId}/package/pickup-otp",
            new { otp = package.PickupOtp },
            package.Driver.Bearer);

        Assert.Equal(HttpStatusCode.OK, picked.StatusCode);
    }

    /// <summary>A correct code never spends an attempt, however many wrong ones came before it.</summary>
    [Fact]
    public async Task A_correct_code_after_four_wrong_ones_still_opens_the_gate()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var package = await harness.DrivePackageToAsync("Accepted");

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var response = await harness.PostAsync(
                $"/v1/rides/{package.RideId}/package/pickup-otp",
                new { otp = WrongCode(package.PickupOtp, attempt) },
                package.Driver.Bearer);

            await ProblemDocument.AssertAsync(response, HttpStatusCode.Unauthorized, "invalid-otp");
        }

        var picked = await harness.PostAsync(
            $"/v1/rides/{package.RideId}/package/pickup-otp",
            new { otp = package.PickupOtp },
            package.Driver.Bearer);

        Assert.Equal(HttpStatusCode.OK, picked.StatusCode);

        await using var connection = await harness.OpenAsync();
        Assert.Equal(
            4,
            await connection.ExecuteScalarAsync<short>(
                "SELECT pickup_otp_attempts FROM rides.rides WHERE id = @RideId;", new { RideId = package.RideId }));

        Assert.DoesNotContain("package.otp_locked", await harness.ReadEventsAsync(package.RideId));
    }

    /// <summary>
    /// DoD item 4. Nobody was at the door, so a photograph is the proof: the ride reaches
    /// <c>PaymentPending</c> through <c>Completed</c> and the artifact is on the row (P-10).
    /// </summary>
    [Fact]
    public async Task A_package_delivered_on_photo_proof_completes_and_the_artifact_is_persisted()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var package = await harness.DrivePackageToAsync("InProgress");
        var photo = RandomNumberGenerator.GetBytes(4_096);

        var response = await UploadAsync(harness, package, photo, lat: 6.9271, lng: 79.8612);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await RideHarness.ReadJsonAsync(response);
        var artifactId = body.GetProperty("artifactId").GetGuid();

        Assert.Equal("PaymentPending", body.GetProperty("state").GetString());

        var (state, _) = await harness.ReadRideAsync(package.RideId);
        Assert.Equal("PaymentPending", state);

        var artifact = Assert.Single(await harness.ReadProofArtifactsAsync(package.RideId));

        Assert.Equal(artifactId, artifact.Id);
        Assert.Equal("delivery_photo", artifact.Kind);

        // The digest is over the bytes as written — it is the tamper evidence a dispute is settled
        // with, so it has to describe the file that actually exists.
        Assert.Equal(SHA256.HashData(photo), artifact.Sha256);

        var stored = new Uri(artifact.StorageUrl).LocalPath;
        Assert.True(File.Exists(stored));
        Assert.Equal(photo, await File.ReadAllBytesAsync(stored, TestContext.Current.CancellationToken));

        // The receipt AL-44 renders says `photo_proof` because an artifact exists; the event is
        // what carries the id.
        var delivered = (await harness.ReadEventPayloadAsync(package.RideId, "package.delivered"))
            .GetProperty("payload");

        Assert.Equal(artifactId, delivered.GetProperty("proofArtifactId").GetGuid());

        // The captured position went with it (migration 0607's `captured_geo`).
        await using var connection = await harness.OpenAsync();
        var captured = await connection.QuerySingleAsync<double?>(
            "SELECT ST_Y(captured_geo::geometry) FROM rides.proof_artifacts WHERE id = @Id;",
            new { Id = artifactId });

        Assert.Equal(6.9271, captured!.Value, 4);
    }

    /// <summary>
    /// A photo is proof of a delivery, so there has to be one under way: a parcel that has not been
    /// picked up cannot be photographed into completion.
    /// </summary>
    [Fact]
    public async Task Photo_proof_is_refused_before_the_parcel_has_been_picked_up()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var package = await harness.DrivePackageToAsync("Accepted");

        var response = await UploadAsync(harness, package, RandomNumberGenerator.GetBytes(64));

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "illegal-transition");

        Assert.Empty(await harness.ReadProofArtifactsAsync(package.RideId));
    }

    [Fact]
    public async Task A_photo_past_the_ceiling_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(
            postgres, new Dictionary<string, string?> { ["Ride:ProofPhotoMaxBytes"] = "65536" });

        var package = await harness.DrivePackageToAsync("InProgress");

        var response = await UploadAsync(harness, package, RandomNumberGenerator.GetBytes(70_000));

        await ProblemDocument.AssertAsync(response, HttpStatusCode.RequestEntityTooLarge, "payload-too-large");

        Assert.Empty(await harness.ReadProofArtifactsAsync(package.RideId));
    }

    /// <summary>Another driver cannot open somebody else's parcel, at either gate.</summary>
    [Fact]
    public async Task Only_the_accepted_driver_may_answer_a_gate()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var package = await harness.DrivePackageToAsync("Accepted");
        var stranger = await harness.CreateDriverAsync("motorbike");

        var response = await harness.PostAsync(
            $"/v1/rides/{package.RideId}/package/pickup-otp",
            new { otp = package.PickupOtp },
            stranger.Bearer);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "not-ride-participant");

        // Not even the attempt counter moved: an unauthorised caller never touches the row.
        await using var connection = await harness.OpenAsync();
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<short>(
                "SELECT pickup_otp_attempts FROM rides.rides WHERE id = @RideId;", new { RideId = package.RideId }));
    }

    /// <summary>The gates belong to a package; a passenger ride starts and completes as it always did.</summary>
    [Fact]
    public async Task A_passenger_ride_has_no_package_gates()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("Accepted");

        var response = await harness.PostAsync(
            $"/v1/rides/{ride.RideId}/package/pickup-otp", new { otp = "1234" }, ride.Driver.Bearer);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "illegal-transition");
    }

    /// <summary>AL-09: the two delivery tiers carry parcels, not people.</summary>
    [Fact]
    public async Task A_truck_cannot_be_booked_for_a_passenger()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passenger = await harness.CreateUserAsync();

        var response = await harness.PostAsync(
            "/v1/rides/request",
            new
            {
                clientRequestId = Guid.NewGuid().ToString(),
                pickup = new { lat = RideHarness.Pickup.Latitude, lng = RideHarness.Pickup.Longitude },
                dropoff = new { lat = RideHarness.Dropoff.Latitude, lng = RideHarness.Dropoff.Longitude },
                vehicleType = "mini_truck",
                fareEstimateToken = harness.IssueFareToken("mini_truck"),
                paymentMethod = "cash",
            },
            passenger.Bearer);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");

        // …and the same tier is fine for a parcel.
        var package = await harness.RequestPackageRideAsync(
            passenger.Bearer, IamLookupStub.UnregisteredPhone(), packageSize: "L", vehicleType: "mini_truck");

        Assert.Equal(HttpStatusCode.Accepted, package.StatusCode);
    }

    [Fact]
    public async Task A_package_needs_a_size_and_a_recipient_to_deliver_to()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var sender = await harness.CreateUserAsync();

        var noSize = await harness.PostAsync(
            "/v1/rides/request",
            new
            {
                clientRequestId = Guid.NewGuid().ToString(),
                kind = "package",
                pickup = new { lat = RideHarness.Pickup.Latitude, lng = RideHarness.Pickup.Longitude },
                dropoff = new { lat = RideHarness.Dropoff.Latitude, lng = RideHarness.Dropoff.Longitude },
                vehicleType = "motorbike",
                fareEstimateToken = harness.IssueFareToken("motorbike", kind: "package"),
                paymentMethod = "cash",
                recipientPhone = IamLookupStub.UnregisteredPhone(),
            },
            sender.Bearer);

        await ProblemDocument.AssertAsync(noSize, HttpStatusCode.BadRequest, "validation-failed");

        var noRecipient = await harness.PostAsync(
            "/v1/rides/request",
            new
            {
                clientRequestId = Guid.NewGuid().ToString(),
                kind = "package",
                pickup = new { lat = RideHarness.Pickup.Latitude, lng = RideHarness.Pickup.Longitude },
                dropoff = new { lat = RideHarness.Dropoff.Latitude, lng = RideHarness.Dropoff.Longitude },
                vehicleType = "motorbike",
                fareEstimateToken = harness.IssueFareToken("motorbike", kind: "package"),
                paymentMethod = "cash",
                packageSize = "S",
            },
            sender.Bearer);

        await ProblemDocument.AssertAsync(noRecipient, HttpStatusCode.BadRequest, "invalid-phone");
    }

    /// <summary>
    /// AL-33's delivery sheets put a call button beside both parties, so the driver's view of a
    /// package carries two numbers rather than one.
    /// </summary>
    [Fact]
    public async Task The_delivery_sheet_carries_both_the_sender_and_the_recipient()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var package = await harness.DrivePackageToAsync("Accepted");

        var response = await harness.GetAsync($"/v1/rides/{package.RideId}", package.Driver.Bearer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var detail = await RideHarness.ReadJsonAsync(response);

        Assert.Equal("package", detail.GetProperty("kind").GetString());
        Assert.Equal("S", detail.GetProperty("packageSize").GetString());
        Assert.Equal("PickupPending", detail.GetProperty("packageStatus").GetString());
        Assert.Equal("Kamala", detail.GetProperty("recipientName").GetString());
        Assert.Equal(package.Sender.Phone, detail.GetProperty("senderPhone").GetString());
        Assert.Equal(package.RecipientPhone, detail.GetProperty("recipientPhone").GetString());

        // The far end of the delivery is what the single `counterpartyPhone` field means here.
        Assert.Equal(package.RecipientPhone, detail.GetProperty("counterpartyPhone").GetString());
    }

    /// <summary>A code that is wrong, deterministically, and never accidentally the right one.</summary>
    private static string WrongCode(string correct, int attempt)
    {
        var candidate = ((int.Parse(correct, System.Globalization.CultureInfo.InvariantCulture) + attempt) % 10_000)
            .ToString("D4", System.Globalization.CultureInfo.InvariantCulture);

        return candidate == correct ? "9999" == correct ? "1111" : "9999" : candidate;
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        RideHarness harness, LivePackage package, byte[] photo, double? lat = null, double? lng = null)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(photo);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

        content.Add(file, "file", "doorstep.jpg");
        content.Add(new StringContent("Left with the security desk"), "note");

        if (lat is { } latitude && lng is { } longitude)
        {
            content.Add(new StringContent(latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)), "lat");
            content.Add(new StringContent(longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)), "lng");
        }

        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/rides/{package.RideId}/package/proof-photo")
        {
            Content = content,
        };

        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", package.Driver.Bearer);

        return await harness.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
