using System.Net;
using System.Text.Json;
using MageRide.E2E.Infrastructure;
using MageRide.TestKit;

namespace MageRide.E2E.Scenarios;

/// <summary>
/// <b>The six SCR-WT pages at <c>passenger.mageride.lk</c> (AL-04, AL-44, AL-48, Epic 25).</b>
/// </summary>
/// <remarks>
/// <para>
/// Everyone on these pages has no MageRide account, and the token in the URL is the whole
/// credential. What is asserted here is therefore mostly about <em>shape</em>: which of the three
/// snapshots a scope gets, what each one may not carry, and what a dead link says — which is
/// nothing at all.
/// </para>
/// <para>
/// <b>All six screens, and where each is driven.</b> There is one route behind SCR-WT-001, -002,
/// -003, -004 and -006 — <c>GET /public/track/{token}</c> — and what distinguishes them is the scope
/// on the row and whether the row is still live. So the coverage is by scope, not by URL:
/// </para>
/// <list type="table">
///   <item><term>SCR-WT-001</term><description>the entry: the scope decides which page you land on,
///     and a token from another contract lands nowhere —
///     <see cref="A_scope_cannot_be_talked_into_another_scopes_page"/></description></item>
///   <item><term>SCR-WT-002</term><description>the package recipient —
///     <c>PackageDeliveryScenario.An_unregistered_recipient_is_SMSed_a_link_and_reads_their_code_off_the_page</c>,
///     which reaches it from the SMS that AL-21 sends</description></item>
///   <item><term>SCR-WT-003</term><description>the pickup confirm — <see cref="WebPickupConfirmScenario"/>,
///     which is its own file because it is a write path with a state machine behind it</description></item>
///   <item><term>SCR-WT-004</term><description>the proxy rider, and the panic button on it —
///     <see cref="SCR_WT_004_gives_a_proxy_rider_the_driver_and_no_word_about_the_bookers_instrument"/>
///     and <see cref="The_web_SOS_identifies_itself_by_token_and_reaches_the_booker"/></description></item>
///   <item><term>SCR-WT-005</term><description>the receipt —
///     <see cref="SCR_WT_005_reports_the_payment_on_a_parcel_that_was_also_photographed"/></description></item>
///   <item><term>SCR-WT-006</term><description>the closed link —
///     <see cref="SCR_WT_006_answers_a_closed_link_with_nothing_about_the_ride"/></description></item>
/// </list>
/// <para>
/// D3' <c>public-bff</c>, D6' I-29.1/I-29.4, US-25.2/25.5/25.6, US-26.2/26.3, D-33, D-34.
/// </para>
/// </remarks>
[Collection<ProxyPackageCollection>]
[Trait("Category", "ProxyPackage")]
public sealed class WebSubviewScenario(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
    : ProxyPackageScenario(postgres, redis, redpanda)
{
    /// <summary>
    /// SCR-WT-004: what a proxy rider is shown, and the two things they are not (AL-48, P-09).
    /// </summary>
    [Fact]
    public Task SCR_WT_004_gives_a_proxy_rider_the_driver_and_no_word_about_the_bookers_instrument() =>
        RunAsync(async (fleet, rides) =>
        {
            var booker = await fleet.CreatePassengerAsync("Wickramasinghe");
            var rider = await fleet.CreatePassengerAsync("Kamala");

            // LankaQR, so the booker's instrument settles it — which is the case P-09 is about.
            var ride = await AcceptedProxyAsync(
                fleet, rides, booker, rider.Phone, paymentMethod: "lankaqr");

            // AL-44/US-8.22: the link is minted when a driver accepts, because until then there is
            // nothing to watch. It arrives by SMS and is never returned by any API.
            var token = SmsGateway.TokenIn(await fleet.Sms.AwaitSmsAsync(rider.Phone));

            var minted = Assert.Single(await fleet.ReadShareTokensAsync(ride.RideId, null));
            Assert.Equal("proxy_rider", minted.Scope);

            var page = await fleet.Web.OpenAsync(token);

            Assert.Equal(HttpStatusCode.OK, page.Status);
            Assert.Equal("ride", page.Json.GetProperty("kind").GetString());
            Assert.Equal("Accepted", page.Json.GetProperty("state").GetString());

            // **AL-48: the real MSISDN, as a `tel:` link.** The masking requirement was withdrawn in
            // full, so "we removed the masking" and "we accidentally emit a masked value" have to be
            // told apart by asserting the number is the driver's own.
            var driver = page.Json.GetProperty("driver");

            Assert.Equal(ride.Driver.Phone, driver.GetProperty("phone").GetString());
            Assert.Equal(ride.Driver.Plate, driver.GetProperty("regNo").GetString());
            Assert.DoesNotContain('*', driver.GetProperty("phone").GetString()!);

            // **P-09: who settles, never what with.** The rider is told the booker is paying and
            // nothing else — `PublicFareResponse` has no field for an instrument, so a later
            // projection cannot put one there.
            var fare = page.Json.GetProperty("fare");

            Assert.Equal("booker", fare.GetProperty("paidBy").GetString());
            Assert.False(fare.TryGetProperty("method", out _));
            Assert.False(fare.TryGetProperty("paymentMethod", out _));
            Assert.False(page.Mentions("lankaqr"), "SCR-WT-004 named the booker's payment instrument (P-09).");

            // And nothing about the booker themselves.
            Assert.False(page.Mentions(booker.Phone), "SCR-WT-004 carried the booker's number.");
            Assert.False(page.Mentions("Wickramasinghe"), "SCR-WT-004 carried the booker's name.");

            // The metering AL-44 exists for: a shared link is unauthenticated, so how often and how
            // recently it was redeemed is the only forensic trail there is.
            var metered = Assert.Single(await fleet.ReadShareTokensAsync(ride.RideId, null));

            Assert.True(metered.AccessCount >= 1);
            Assert.NotNull(metered.LastAccessAt);
        });

    /// <summary>
    /// <b>The web SOS: no account, no session, and somebody is still told (US-25.5, D-33).</b>
    /// </summary>
    [Fact]
    public Task The_web_SOS_identifies_itself_by_token_and_reaches_the_booker() =>
        RunAsync(async (fleet, rides) =>
        {
            var booker = await fleet.CreatePassengerAsync("Wickramasinghe");
            var rider = await fleet.CreatePassengerAsync("Kamala");

            var ride = await AcceptedProxyAsync(fleet, rides, booker, rider.Phone);
            var token = SmsGateway.TokenIn(await fleet.Sms.AwaitSmsAsync(rider.Phone));

            // The browser's Geolocation API supplies these. There is no server-side fallback to the
            // driver's last position: the row must say where the *person* said they were.
            var raised = await fleet.Web.SosAsync(token, 6.9271, 79.8612);

            Assert.Equal(HttpStatusCode.Accepted, raised.Status);
            Assert.NotEqual(Guid.Empty, raised.Json.GetProperty("sosId").GetGuid());

            // `smsStatus` beside a nullable `dispatchedAt`, which is the difference between somebody
            // having been told and nobody having been.
            Assert.Equal("Dispatched", raised.Json.GetProperty("smsStatus").GetString());
            Assert.NotEqual(JsonValueKind.Null, raised.Json.GetProperty("dispatchedAt").ValueKind);

            // public-bff forwarded; safety-svc wrote. One writer for `safety.sos_events`, and the
            // booker's number was resolved inside it — public-bff never learns one.
            var recorded = Assert.Single(await fleet.ReadWebSosAsync(ride.RideId));

            Assert.Equal("web", recorded.Source);
            Assert.Null(recorded.UserId);
            Assert.Equal(token, recorded.ShareToken);
            Assert.Equal(6.9271, recorded.Lat, precision: 4);

            // D-33: the alert goes to the booker's registered mobile, and the SMS really left the
            // platform through the same gateway everything else does.
            var alert = await fleet.Sms.AwaitSmsAsync(booker.Phone);
            Assert.Contains("SOS", alert.Body, StringComparison.OrdinalIgnoreCase);

            // A pickup_confirm link cannot raise one: there is no ride, no booker and nobody in a
            // vehicle, and SCR-WT-003 draws no SOS button for exactly that reason.
            var pickupToken = await PickupConfirmTokenAsync(fleet, booker);

            var refused = await fleet.Web.SosAsync(pickupToken, 6.9271, 79.8612);
            Assert.InRange((int)refused.Status, 400, 499);
            Assert.False(refused.Mentions(ride.RideId.ToString()));
        });

    /// <summary>
    /// SCR-WT-005: the receipt, derived from three facts and stored nowhere (US-25.6).
    /// </summary>
    /// <remarks>
    /// Driven through a COD parcel that was <em>also</em> photographed, because that is where the
    /// precedence rule bites: money outranks evidence, so a receipt for a delivery with both reports
    /// the payment. A receipt claiming a photographed doorstep on a parcel that was paid for would
    /// answer a different question from the one it is opened to answer.
    /// </remarks>
    [Fact]
    public Task SCR_WT_005_reports_the_payment_on_a_parcel_that_was_also_photographed() =>
        RunAsync(async (fleet, rides) =>
        {
            var recipientPhone = ProxyPackageFleet.UnregisteredPhone();
            var ride = await AcceptedPackageAsync(fleet, rides, recipientPhone, paymentMethod: "cod");

            using (var picked = await fleet.PickupOtpAsync(ride, ride.PickupOtp))
            {
                Assert.Equal(HttpStatusCode.OK, picked.StatusCode);
            }

            var token = SmsGateway.TokenIn(await fleet.Sms.AwaitSmsAsync(recipientPhone));

            // Asking before the journey ends is a 409 with a name, not a blank page: the recipient
            // needs to tell "come back when the trip ends" from any other conflict.
            var early = await fleet.Web.ReceiptAsync(token);

            Assert.Equal(HttpStatusCode.Conflict, early.Status);
            Assert.Equal("receipt-not-ready", early.ProblemCode);

            using (var photographed = await fleet.ProofPhotoAsync(ride, ride.Dropoff))
            {
                Assert.Equal(HttpStatusCode.Created, photographed.StatusCode);
            }

            // Still not receiptable: `PaymentPending` is the journey being over, not the money.
            Assert.Equal(HttpStatusCode.Conflict, (await fleet.Web.ReceiptAsync(token)).Status);

            // fare-svc's `POST /v1/fare/calculate`, which is the hop ride-svc's completion does not
            // make — C120 found the same gap and stands in for it the same way, through the internal
            // route rather than by writing a payment row.
            await fleet.PriceAsync(ride.RideId);

            using (var banked = await fleet.CodCollectedAsync(ride, 45_000))
            {
                Assert.Equal(HttpStatusCode.OK, banked.StatusCode);
            }

            var receipt = await fleet.Web.ReceiptAsync(token);

            Assert.Equal(HttpStatusCode.OK, receipt.Status);
            Assert.Equal("package", receipt.Json.GetProperty("kind").GetString());
            Assert.Equal("CashOnDeliveryCollected", receipt.Json.GetProperty("state").GetString());

            // The precedence rule, on a delivery that has both a photograph and a payment.
            Assert.Equal("cod_collected", receipt.Json.GetProperty("proof").GetString());

            // ---- a gap, asserted as a gap -------------------------------------------------------
            // **Nothing moves `fares.ride_payments` to `CashOnDeliveryCollected`, so the receipt for
            // a COD parcel carries no figure.** ride-svc's `cod-collected` says outright that it
            // writes no payment row ("no `fares.ride_payments` row exists yet — fare-svc is
            // C049/C050") and publishes `payment.cod_collected` and `ride.settled` instead;
            // **fare-svc has no consumer at all** — it is called, it does not listen — so the row
            // `calculate` opened stays at its booking-time state for ever. `SettledPaymentStates`
            // names `CashOnDeliveryCollected`, D4' declares it and P-08 says it "posts driver
            // earning identically to CashSettled", and no component writes it.
            //
            // Recorded in the C122 handoff. The assertion is deliberately two-sided: it fails now if
            // the amount appears, which is how the day somebody wires the consumer gets noticed here
            // rather than months later.
            var payment = Assert.Single(await fleet.ReadPaymentsAsync(ride.RideId));

            Assert.Equal("cod", payment.Method);
            Assert.NotEqual("CashOnDeliveryCollected", payment.State);

            Assert.False(
                receipt.Json.TryGetProperty("totalMinor", out var total)
                && total.ValueKind is not JsonValueKind.Null,
                $"The COD receipt now carries a figure ({total}). The gap this test records is closed — "
                + "delete the ledger entry and assert the amount instead.");

            // A ride with no settled figure carries no fare block rather than a zero: `Rs 0.00` on a
            // receipt reads as "this was free".
            Assert.False(receipt.Json.TryGetProperty("currency", out var currency)
                         && currency.ValueKind is not JsonValueKind.Null);

            // **No `driver.phone` on a receipt**, and that is not an oversight: AL-48's `tel:` link
            // exists so a recipient can reach a driver who is on the way to them. Once the parcel is
            // delivered there is nothing to call about, and a receipt is a document that gets
            // forwarded.
            Assert.False(receipt.Mentions(ride.Driver.Phone), "The receipt carried the driver's number.");

            // Every value is derived and none is stored, so reading it twice says the same thing.
            var reprint = await fleet.Web.ReceiptAsync(token);
            Assert.Equal(receipt.Json.GetProperty("proof").GetString(), reprint.Json.GetProperty("proof").GetString());
        });

    /// <summary>
    /// <b>C122's third definition-of-done item: SCR-WT-006 exposes no ride data.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The status code is the least interesting half. What is asserted is the <em>body</em>: the ride
    /// id, the state, the driver's name, their number, the plate and both coordinates are each
    /// checked for absence, because a 410 that carried a payload would be indistinguishable from a
    /// working page to anything except a person reading it.
    /// </para>
    /// <para>
    /// The token is closed the way the platform closes one — safety-svc's trip-end hook, which is
    /// the component that owns the table — rather than by writing <c>revoked_at</c> from here.
    /// </para>
    /// </remarks>
    [Fact]
    public Task SCR_WT_006_answers_a_closed_link_with_nothing_about_the_ride() =>
        RunAsync(async (fleet, rides) =>
        {
            var recipientPhone = ProxyPackageFleet.UnregisteredPhone();
            var ride = await AcceptedPackageAsync(fleet, rides, recipientPhone);

            using (var picked = await fleet.PickupOtpAsync(ride, ride.PickupOtp))
            {
                Assert.Equal(HttpStatusCode.OK, picked.StatusCode);
            }

            var token = SmsGateway.TokenIn(await fleet.Sms.AwaitSmsAsync(recipientPhone));

            // It works first, so what follows is a claim about the closure rather than about a page
            // that never worked.
            var live = await fleet.Web.OpenAsync(token);

            Assert.Equal(HttpStatusCode.OK, live.Status);
            Assert.True(live.Mentions(ride.Driver.Plate), "The live page did not show the plate it is about to stop showing.");

            using (var closed = await fleet.CloseTripSharesAsync(ride.RideId))
            {
                Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
            }

            // ---- every door, and every one of them says nothing --------------------------------
            foreach (var (what, page) in new[]
                     {
                         ("the snapshot", await fleet.Web.OpenAsync(token)),
                         ("the live feed", await fleet.Web.PollAsync(token)),
                         ("the receipt", await fleet.Web.ReceiptAsync(token)),
                     })
            {
                Assert.Equal(HttpStatusCode.Gone, page.Status);
                Assert.Equal("token-expired-or-revoked", page.ProblemCode);

                foreach (var secret in new[]
                         {
                             ride.RideId.ToString(),
                             ride.Driver.Plate,
                             ride.Driver.Phone,
                             ride.Passenger.Phone,
                             recipientPhone,
                             "E2E Driver",
                             "InProgress",
                             ride.Pickup.Latitude.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
                             ride.Dropoff.Longitude.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
                         })
                {
                    Assert.False(
                        page.Mentions(secret),
                        $"{what} answered 410 and still carried '{secret}': {page.Body}");
                }
            }

            // The metering counted the dead hits — which is exactly what it is for: somebody still
            // holding a revoked link is the pattern AL-44 wants surfaced.
            var burned = Assert.Single(await fleet.ReadShareTokensAsync(ride.RideId, null));

            Assert.NotNull(burned.RevokedAt);
            Assert.True(burned.AccessCount >= 4, $"Only {burned.AccessCount} accesses were metered.");
        });

    /// <summary>
    /// <b>SCR-WT-001:</b> a token cannot be talked into a page it is not for, and a stranger's link
    /// is not even acknowledged.
    /// </summary>
    /// <remarks>
    /// The entry screen is where a scope becomes a page, so this is what it means for that screen to
    /// be correct: the response is shaped strictly by the scope on the row — no query parameter, no
    /// <c>Accept</c> negotiation and no request field selects a variant — and a credential from
    /// another contract does not open a door here at all. D-34's own share link is refused as
    /// <em>unknown</em> rather than "that belongs elsewhere", because saying so would make the route
    /// an oracle over which links are live.
    /// </remarks>
    [Fact]
    public Task A_scope_cannot_be_talked_into_another_scopes_page() =>
        RunAsync(async (fleet, rides) =>
        {
            var recipientPhone = ProxyPackageFleet.UnregisteredPhone();
            var ride = await AcceptedPackageAsync(fleet, rides, recipientPhone);

            using (var picked = await fleet.PickupOtpAsync(ride, ride.PickupOtp))
            {
                Assert.Equal(HttpStatusCode.OK, picked.StatusCode);
            }

            var packageToken = SmsGateway.TokenIn(await fleet.Sms.AwaitSmsAsync(recipientPhone));

            // A package recipient may not answer a pickup request: a 403, because the caller holds a
            // token this surface recognises and is being told this particular door is not theirs.
            var confirm = await fleet.Web.ConfirmPickupAsync(packageToken, 6.9271, 79.8612);
            Assert.Equal(HttpStatusCode.Forbidden, confirm.Status);

            var decline = await fleet.Web.DeclinePickupAsync(packageToken);
            Assert.Equal(HttpStatusCode.Forbidden, decline.Status);

            // ---- D-34's share link belongs to a different contract -----------------------------
            // Minted through the route a passenger actually uses, so this is a live token being
            // refused rather than a string nobody issued.
            var tripView = await fleet.ShareTripAsync(ride.Passenger, ride.RideId);
            var elsewhere = await fleet.Web.OpenAsync(tripView);

            Assert.Equal(HttpStatusCode.NotFound, elsewhere.Status);
            Assert.False(
                elsewhere.Mentions(ride.RideId.ToString()),
                "The refusal of a trip_view link named the ride it belongs to.");

            // ---- and a link nobody ever issued -------------------------------------------------
            var invented = await fleet.Web.OpenAsync("Zm9yZ2VkLXRva2VuLXRoYXQtd2FzLW5ldmVyLW1pbnRlZA");

            Assert.Equal(HttpStatusCode.NotFound, invented.Status);
            Assert.False(invented.Mentions(ride.Driver.Plate));

            // A value too short to have been minted is refused on shape, before the token store or
            // the per-IP budget is touched at all.
            Assert.Equal(HttpStatusCode.NotFound, (await fleet.Web.OpenAsync("short")).Status);
        });

    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A live <c>pickup_confirm</c> token, obtained the only way one can be: by asking an
    /// unregistered rider where they are and reading the SMS.
    /// </summary>
    private static async Task<string> PickupConfirmTokenAsync(ProxyPackageFleet fleet, Passenger booker)
    {
        var riderPhone = ProxyPackageFleet.UnregisteredPhone();
        var request = await fleet.RequestLocationAsync(booker, riderPhone);

        Assert.Equal("RiderNotRegistered", request.State);

        return SmsGateway.TokenIn(await fleet.Sms.AwaitSmsAsync(riderPhone));
    }
}
