using System.Net;
using Dapper;
using MageRide.E2E.Infrastructure;
using MageRide.Shared.Time;
using MageRide.TestKit;

namespace MageRide.E2E.Scenarios;

/// <summary>
/// Epic 23 — the Mode B subscriber's monthly fare, on its way to the fleet owner.
/// </summary>
/// <remarks>
/// <para>
/// <b>MageRide holds none of this money, takes no cut and writes no ledger entry for it</b> (§18b).
/// That is the whole shape of the component and it is why the most important assertion in this file
/// is an absence: after five payments by five different methods, the double-entry ledger has not
/// moved at all. It is held structurally as well — migration 1202 gives
/// <c>subscription.payments</c> no column that could hold a posting id, which
/// <see cref="Subscription_money_never_enters_the_platform_ledger"/> asserts against
/// <c>information_schema</c> rather than against a service's good behaviour.
/// </para>
/// <para>
/// <b>Where the money goes is a verified payout profile and nothing else</b> (AL-49). Every
/// organisation in this file is built through fleet-svc's own routes in BR-31.1's order — register,
/// submit the bank details, upload the LankaQR image, have a Verification Officer approve the org
/// (which is what verifies the profile), then onboard a Paid vehicle. Skip the third step and the
/// fourth is refused, which is the first thing this scenario asserts.
/// </para>
/// <para>
/// <b>Two of the five methods have no machine confirmation at all.</b> An online transfer is
/// confirmed by the owner looking at a screenshot and cash by the owner saying it arrived — so the
/// row is a statement of what the two parties agree happened, and what moves the billing cycle on is
/// that agreement. Which is exactly why "only the fleet Owner can mark it received" (US-23.6) is a
/// rule with teeth rather than a UI detail.
/// </para>
/// </remarks>
[Collection<MoneyCollection>]
[Trait("Category", "Money")]
public sealed class ModeBSubscriptionPaymentScenario(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
    : MoneyScenario(postgres, redis, redpanda)
{
    /// <summary>AL-49 / BR-31.1 — a Paid vehicle needs a verified payout profile before it exists.</summary>
    /// <remarks>
    /// <para>
    /// The gate is on <em>collecting</em>, not on joining: a Paid vehicle whose org has no verified
    /// profile still accepts passengers, it just cannot take their money. Blocking the accept would
    /// deny a child a seat on a school van over the owner's bank paperwork. What BR-31.1 does block
    /// is the classification itself — which is the earlier and better place, because a vehicle that
    /// was never Paid has no subscriber expecting to pay.
    /// </para>
    /// <para>
    /// Free is ungated in both directions, and that is not an oversight: an office shuttle collects
    /// nothing, so there is nowhere for its money to go wrong.
    /// </para>
    /// </remarks>
    [Fact]
    public Task A_vehicle_cannot_be_made_Paid_until_a_verification_officer_has_approved_the_bank_details() =>
        RunAsync(async (fleet, parties) =>
        {
            // Approved on its KYC, with no bank details submitted at all — the ordinary way an
            // organisation comes to be onboarding vehicles with nothing verified to collect into.
            // US-13.A7's gate has to be past first: an unapproved org onboards nothing, Paid or
            // Free, so the BR-31.1 refusal below is unreachable from there.
            var org = await fleet.CreateUnapprovedFleetOrgAsync(withPayoutProfile: false);
            parties.AddRange(org.FleetId, org.OwnerId);

            await fleet.ApproveFleetAsync(org.FleetId);

            using (var paid = await MoneyFleet.PostAsync(
                fleet.FleetClient,
                $"/v1/fleets/{org.FleetId}/vehicles",
                new
                {
                    registrationNumber = MoneyFleet.NextPlate(),
                    vehicleType = "van",
                    mode = "B",
                    modeBBilling = "paid",
                    defaultMonthlyFareMinor = 250_000,
                },
                org.OwnerBearer))
            {
                Assert.Equal(HttpStatusCode.Conflict, paid.StatusCode);
                Assert.Equal("payout-profile-not-verified", await MoneyFleet.ProblemCodeAsync(paid));
            }

            // Free is ungated: the office shuttle collects nothing, so there is nothing to route.
            using (var free = await MoneyFleet.PostAsync(
                fleet.FleetClient,
                $"/v1/fleets/{org.FleetId}/vehicles",
                new
                {
                    registrationNumber = MoneyFleet.NextPlate(),
                    vehicleType = "van",
                    mode = "B",
                    modeBBilling = "free",
                },
                org.OwnerBearer))
            {
                await MoneyFleet.AssertStatusAsync(free, HttpStatusCode.Created, "onboarding a Free Mode B vehicle");
            }
        });

    /// <summary>
    /// US-23.3 — LankaQR, both ways, rendering the owner's own bank QR rather than the platform's.
    /// </summary>
    /// <remarks>
    /// AL-49's pay sheet: the deep-link and the scan method both resolve to the same thing, a signed
    /// link to the <em>owner's</em> bank-app QR image. No platform merchant appears anywhere on this
    /// path, which is the point — the money is a pass-through and MageRide is not the payee.
    /// </remarks>
    [Fact]
    public Task A_LankaQR_pay_sheet_shows_the_owners_own_bank_QR_and_no_platform_merchant() =>
        RunAsync(async (fleet, parties) =>
        {
            var org = await fleet.CreateFleetOrgAsync();
            var passenger = await fleet.CreatePassengerAsync();
            parties.AddRange(org.FleetId, org.OwnerId, passenger.Id);

            var subscription = await fleet.SubscribeAsync(org, passenger);

            Assert.Equal(250_000, subscription.MonthlyFareMinor);

            var lankaQrLink = string.Empty;

            foreach (var method in new[] { "lankaqr_deeplink", "lankaqr_scan" })
            {
                using var sheet = await fleet.PaySubscriptionAsync(subscription, method);

                await MoneyFleet.AssertOkAsync(sheet, $"opening the {method} pay sheet");

                var body = await MoneyFleet.ReadJsonAsync(sheet);

                Assert.Equal(method, body.GetProperty("method").GetString());
                Assert.Equal("initiated", body.GetProperty("status").GetString());
                Assert.Equal(250_000, body.GetProperty("amountMinor").GetInt64());

                var payTo = body.GetProperty("payTo");
                var image = payTo.GetProperty("lankaqrImageUrl").GetString();

                Assert.False(
                    string.IsNullOrWhiteSpace(image),
                    "AL-49's pay sheet renders the owner's bank-app LankaQR image. Without it the "
                    + "passenger has nothing to scan and the working rail is unusable.");

                // The signed link the app follows. The signature is the credential and the kind is
                // inside it — without that, a link to a slip could be re-pointed at a payout profile
                // by editing one path segment, and both are somebody's private document.
                Assert.Contains("/v1/mode-b/files/lankaqr/", image, StringComparison.Ordinal);
                Assert.Contains(org.PayoutProfileId.ToString(), image, StringComparison.Ordinal);

                // No bank details on the QR rails, and no platform anything. The kernel omits nulls,
                // so the field is absent rather than null.
                Assert.False(
                    payTo.TryGetProperty("accountNo", out var accountNo) && accountNo.ValueKind != JsonNull,
                    "A LankaQR pay sheet carries the owner's QR and nothing else: the account number "
                    + "belongs to the online-transfer sheet, and printing it on both would put a bank "
                    + "account on a screen nobody needs it on.");

                lankaQrLink = image!;
            }

            // The image resolves for a caller holding the signature, and not for one who edits it.
            var link = lankaQrLink;

            using (var fetched = await MoneyFleet.GetAsync(fleet.SubscriptionClient, link, null))
            {
                await MoneyFleet.AssertOkAsync(fetched, "following the signed LankaQR link");
            }

            // The kind is inside the signature, so re-pointing the link at a transfer slip by
            // editing one path segment invalidates it — which is the whole reason it is signed over
            // more than the id. Both documents are somebody's private paperwork.
            using (var tampered = await MoneyFleet.GetAsync(
                fleet.SubscriptionClient, link.Replace("/lankaqr/", "/slip/", StringComparison.Ordinal), null))
            {
                Assert.Equal(HttpStatusCode.Forbidden, tampered.StatusCode);
            }

            await AssertNoPlatformMoneyAsync(fleet, org, passenger);
        });

    /// <summary>
    /// US-23.4 — an online transfer waits, with its evidence, until the owner confirms it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one method whose settlement is a human reading a screenshot. Three things follow and all
    /// three are asserted: the pay sheet carries the owner's bank details rather than a QR, the slip
    /// puts the payment into <c>pending_verification</c> where nothing else may re-open it, and the
    /// owner's confirm is what both marks the month paid <em>and</em> rolls the billing cycle on.
    /// </para>
    /// <para>
    /// <b>The due date moves only for the settlement that actually happened.</b> Every path advances
    /// it from the row returned by a <em>guarded</em> UPDATE, so a second confirm finds the month
    /// already paid, gets no row, and does not advance again — a double advance is a free month, and
    /// it is the one arithmetic error here that costs the fleet owner money.
    /// </para>
    /// </remarks>
    [Fact]
    public Task An_online_transfer_is_pending_verification_until_the_owner_confirms_the_slip() =>
        RunAsync(async (fleet, parties) =>
        {
            var org = await fleet.CreateFleetOrgAsync();
            var passenger = await fleet.CreatePassengerAsync();
            parties.AddRange(org.FleetId, org.OwnerId, passenger.Id);

            var subscription = await fleet.SubscribeAsync(org, passenger);
            var dueBefore = await fleet.ReadNextDueAsync(subscription.SubscriptionId);

            Guid paymentId;

            using (var sheet = await fleet.PaySubscriptionAsync(subscription, "online_transfer"))
            {
                await MoneyFleet.AssertOkAsync(sheet, "opening the online-transfer pay sheet");

                var body = await MoneyFleet.ReadJsonAsync(sheet);

                paymentId = body.GetProperty("paymentId").GetGuid();

                var payTo = body.GetProperty("payTo");

                Assert.Equal("Commercial Bank of Ceylon", payTo.GetProperty("bank").GetString());
                Assert.Equal("Kollupitiya", payTo.GetProperty("branch").GetString());
                Assert.Equal("E2E Transport (Pvt) Ltd", payTo.GetProperty("accountHolderName").GetString());
            }

            // Nothing to confirm yet: only a slip the passenger has uploaded can be confirmed.
            using (var early = await MoneyFleet.PostAsync(
                fleet.SubscriptionClient, $"/v1/mode-b/payments/{paymentId}/confirm", new { }, org.OwnerBearer))
            {
                Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
            }

            using (var slip = await fleet.UploadTransferSlipAsync(paymentId, passenger))
            {
                await MoneyFleet.AssertOkAsync(slip, "the passenger attaching the transfer slip");

                var body = await MoneyFleet.ReadJsonAsync(slip);

                Assert.Equal("pending_verification", body.GetProperty("status").GetString());
                Assert.False(
                    string.IsNullOrWhiteSpace(body.GetProperty("slipUrl").GetString()),
                    "The slip's signed link is the evidence the owner's confirm is made on.");
            }

            // The sheet cannot be re-issued while the slip is with the owner: that would clear it and
            // lose the evidence the confirm is waiting on.
            using (var reopened = await fleet.PaySubscriptionAsync(subscription, "lankaqr_deeplink"))
            {
                Assert.Equal(HttpStatusCode.Conflict, reopened.StatusCode);
            }

            // And it is the owner's to confirm, nobody else's — not even the passenger who paid.
            using (var notOwner = await MoneyFleet.PostAsync(
                fleet.SubscriptionClient, $"/v1/mode-b/payments/{paymentId}/confirm", new { }, passenger.Bearer))
            {
                Assert.Equal(HttpStatusCode.Forbidden, notOwner.StatusCode);
            }

            using (var confirmed = await MoneyFleet.PostAsync(
                fleet.SubscriptionClient, $"/v1/mode-b/payments/{paymentId}/confirm", new { }, org.OwnerBearer))
            {
                await MoneyFleet.AssertOkAsync(confirmed, "the owner confirming the transfer slip");
                Assert.Equal("paid", (await MoneyFleet.ReadJsonAsync(confirmed)).GetProperty("status").GetString());
            }

            var payment = Assert.Single(await fleet.ReadSubscriptionPaymentsAsync(subscription.SubscriptionId));

            Assert.Equal("online_transfer", payment.Method);
            Assert.Equal("paid", payment.Status);
            Assert.Equal(org.OwnerId, payment.ConfirmedBy);
            Assert.NotNull(payment.PaidAt);
            Assert.NotNull(payment.SlipUrl);

            var dueAfter = await fleet.ReadNextDueAsync(subscription.SubscriptionId);

            Assert.True(
                dueAfter > dueBefore,
                $"The billing cycle did not move: it was due {dueBefore} and is still due {dueAfter}.");

            // A second confirm finds the month already paid and does not advance the cycle again.
            using (var again = await MoneyFleet.PostAsync(
                fleet.SubscriptionClient, $"/v1/mode-b/payments/{paymentId}/confirm", new { }, org.OwnerBearer))
            {
                Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            }

            Assert.Equal(dueAfter, await fleet.ReadNextDueAsync(subscription.SubscriptionId));

            await AssertNoPlatformMoneyAsync(fleet, org, passenger);
        });

    /// <summary>
    /// US-23.6 — cash is handed to a collector, and <b>only the Owner</b> may say it arrived.
    /// </summary>
    /// <remarks>
    /// Two halves of one rule. A passenger who could record a cash payment would be marking their own
    /// month paid, so <c>POST …/pay</c> refuses the method outright; and "only the fleet Owner can
    /// mark it received" is resolved against the vehicle rather than from a role claim, because
    /// <c>fleet_owner</c> says somebody runs <em>a</em> fleet, not <em>this</em> vehicle.
    /// </remarks>
    [Fact]
    public Task Cash_is_marked_received_by_the_owner_and_by_nobody_else() =>
        RunAsync(async (fleet, parties) =>
        {
            var org = await fleet.CreateFleetOrgAsync();
            var passenger = await fleet.CreatePassengerAsync();
            parties.AddRange(org.FleetId, org.OwnerId, passenger.Id);

            var subscription = await fleet.SubscribeAsync(org, passenger);
            var dueBefore = await fleet.ReadNextDueAsync(subscription.SubscriptionId);

            // Not a method a passenger may choose.
            using (var declared = await fleet.PaySubscriptionAsync(subscription, "cash"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, declared.StatusCode);
            }

            Assert.Empty(await fleet.ReadSubscriptionPaymentsAsync(subscription.SubscriptionId));

            // Nor may the subscriber mark their own cash received. The path names the *grant* —
            // the roster row — because Epic 23's access is per vehicle (AL-23) and a passenger who
            // left and rejoined keeps one slot and one ledger on the owner's screen.
            var markCash = $"/v1/mode-b/{org.VehicleId}/subscribers/{subscription.GrantId}/mark-cash";

            using (var notOwner = await MoneyFleet.PostAsync(
                fleet.SubscriptionClient, markCash, new { amountMinor = 250_000 }, passenger.Bearer))
            {
                Assert.Equal(HttpStatusCode.Forbidden, notOwner.StatusCode);
            }

            using (var marked = await MoneyFleet.PostAsync(
                fleet.SubscriptionClient, markCash, new { amountMinor = 250_000 }, org.OwnerBearer))
            {
                await MoneyFleet.AssertOkAsync(marked, "the owner marking cash received");

                var body = await MoneyFleet.ReadJsonAsync(marked);

                Assert.Equal("cash", body.GetProperty("method").GetString());
                Assert.Equal("paid", body.GetProperty("status").GetString());
                Assert.Equal(250_000, body.GetProperty("amountMinor").GetInt64());
            }

            var payment = Assert.Single(await fleet.ReadSubscriptionPaymentsAsync(subscription.SubscriptionId));

            Assert.Equal("cash", payment.Method);
            Assert.Equal(org.OwnerId, payment.ConfirmedBy);

            Assert.True(
                await fleet.ReadNextDueAsync(subscription.SubscriptionId) > dueBefore,
                "Marking cash received settles the month, so the cycle rolls on exactly as a confirmed "
                + "transfer does.");

            // The passenger's own history shows it as paid (US-23.9, SCR-PA-025b).
            using (var history = await MoneyFleet.GetAsync(
                fleet.SubscriptionClient,
                $"/v1/mode-b/subscriptions/{subscription.SubscriptionId}/payments",
                passenger.Bearer))
            {
                await MoneyFleet.AssertOkAsync(history, "the subscriber's payment history");

                var row = (await MoneyFleet.ReadJsonAsync(history)).GetProperty("items").EnumerateArray().Single();

                Assert.Equal("cash", row.GetProperty("method").GetString());
                Assert.Equal("paid", row.GetProperty("status").GetString());
            }

            await AssertNoPlatformMoneyAsync(fleet, org, passenger);
        });

    /// <summary>
    /// R-19 — a gateway confirmation settles a subscription month once, and never advances twice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The redelivery is answered <c>200</c> either way, because that is what stops a provider
    /// retrying for ever — so the assertion that matters is not the status code but the due date: a
    /// double advance is a free month, which is the one arithmetic error here that costs the fleet
    /// owner money.
    /// </para>
    /// <para>
    /// <b>The OnePay session is not opened, and its absence is the platform's own decision.</b>
    /// Creating one would have to bind a merchant account to a <em>fleet</em>, and no schema does —
    /// <c>registry.driver_payouts</c> was per driver and AL-57 retired it — so opening one against
    /// the platform's merchant would route a passenger's fare into MageRide's account, which the
    /// pass-through fence forbids outright. So <c>pay</c> records the payment and returns no
    /// redirect, and the callback half settles a session opened elsewhere. Asserted rather than
    /// worked around; recorded in the C048 handoff and re-raised in C123's.
    /// </para>
    /// </remarks>
    [Fact]
    public Task A_gateway_confirmation_settles_a_subscription_month_exactly_once() =>
        RunAsync(async (fleet, parties) =>
        {
            var org = await fleet.CreateFleetOrgAsync();
            var passenger = await fleet.CreatePassengerAsync();
            parties.AddRange(org.FleetId, org.OwnerId, passenger.Id);

            var subscription = await fleet.SubscribeAsync(org, passenger);
            var dueBefore = await fleet.ReadNextDueAsync(subscription.SubscriptionId);

            Guid paymentId;

            using (var sheet = await fleet.PaySubscriptionAsync(subscription, "onepay"))
            {
                await MoneyFleet.AssertOkAsync(sheet, "opening the OnePay pay sheet");

                var body = await MoneyFleet.ReadJsonAsync(sheet);

                paymentId = body.GetProperty("paymentId").GetGuid();

                Assert.False(
                    body.TryGetProperty("redirectUrl", out var redirect) && redirect.ValueKind != JsonNull,
                    "No per-org OnePay merchant binding exists in any schema, so no session is opened and "
                    + "the passenger has no redirect to follow. If one has appeared, C048's gap is closed "
                    + "and this assertion should become a real gateway round-trip.");
            }

            var reference = $"mode-b-{Guid.NewGuid():N}";

            var callback = new
            {
                providerTransactionId = reference,
                paymentId = paymentId.ToString(),
                status = "SUCCESS",
                amountMinor = 250_000,
            };

            for (var delivery = 1; delivery <= 2; delivery++)
            {
                using var confirmed = await fleet.Acquirer.ConfirmAsync(
                    fleet.SubscriptionCallbackUrl("onepay"), MoneyFleet.OnepayWebhookSecret, callback);

                await MoneyFleet.AssertOkAsync(confirmed, $"delivery {delivery} of the gateway confirmation");
            }

            var payment = Assert.Single(await fleet.ReadSubscriptionPaymentsAsync(subscription.SubscriptionId));

            Assert.Equal("paid", payment.Status);
            Assert.Equal(reference, payment.GatewayRef);

            var dueAfter = await fleet.ReadNextDueAsync(subscription.SubscriptionId);

            Assert.True(dueAfter > dueBefore, "The settled month rolls the cycle on once.");

            // ONE cycle and not two — which is what "exactly once" means for the two deliveries
            // above — asserted as a period LENGTH rather than as a date.
            //
            // This was `Assert.Equal(dueBefore.Value.AddMonths(1), dueAfter.Value)`, and adding a
            // month to the previous due date is not the rule the platform implements.
            // `SubscriptionCycles.Advance` re-derives the anniversary from `join_day` every time,
            // deliberately: a subscriber who joined on the 31st has no anniversary in February, and
            // advancing from the clamped 28th would pin them to the 28th for ever. The two readings
            // agree on most days and diverge whenever the join day is the 28th, 29th or 30th —
            // **13 days of a year**. This scenario subscribes at "now", so it was a test that failed
            // on those 13 and passed on the other 352, which is how it came to block an unrelated
            // merge on 30 August 2026 having last run green on the 27th.
            //
            // The arithmetic is not this test's to check, and is already covered against PINNED
            // dates — February and the 31st-joiner included — by
            // `Subscription.Api.Tests/Unit/SubscriptionCycleTests.cs`. What is genuinely
            // end-to-end here is that the webhook path applied that rule ONCE across two deliveries
            // of the same callback. One cycle spans 28-31 days and two would span 56-62, so the two
            // ranges cannot be confused and the bound needs no date in order to be true.
            Assert.InRange(dueAfter!.Value.DayNumber - dueBefore!.Value.DayNumber, 28, 31);

            // Unsigned credits nothing here either: the money it would falsely settle is the fleet
            // owner's, which is why there is no accept-unsigned mode on this rail.
            using (var unsigned = await fleet.Acquirer.ConfirmUnsignedAsync(
                fleet.SubscriptionCallbackUrl("lankaqr"), callback))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
            }

            await AssertNoPlatformMoneyAsync(fleet, org, passenger);
        });

    /// <summary>
    /// AL-24 / BR-23.8 — a <b>Free</b> Mode B vehicle has no fare, and no payment UI to refuse one at.
    /// </summary>
    /// <remarks>
    /// The other half of the Service-payment setting (renamed from "classification" by US-27.4 —
    /// a UI label only; the API and the column are unchanged). An office or staff transport collects
    /// nothing, so a passenger asking to pay one is not making an error about the request body — they
    /// are asking about a vehicle whose whole classification says there is nothing to pay. That is a
    /// <c>409</c> naming the classification rather than a <c>400</c> about the method.
    /// </remarks>
    [Fact]
    public Task A_Free_Mode_B_vehicle_has_no_fare_to_pay() =>
        RunAsync(async (fleet, parties) =>
        {
            var org = await fleet.CreateFleetOrgAsync();
            var passenger = await fleet.CreatePassengerAsync();
            parties.AddRange(org.FleetId, org.OwnerId, passenger.Id);

            // The same organisation, a second vehicle, classified Free — the office shuttle beside
            // the school van.
            var shuttle = await fleet.AddModeBVehicleAsync(org, "free");
            var subscription = await fleet.SubscribeAsync(org with { VehicleId = shuttle }, passenger);

            Assert.Equal(0, subscription.MonthlyFareMinor);

            foreach (var method in new[] { "lankaqr_deeplink", "onepay", "online_transfer" })
            {
                using var refused = await fleet.PaySubscriptionAsync(subscription, method);

                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            }

            Assert.Empty(await fleet.ReadSubscriptionPaymentsAsync(subscription.SubscriptionId));
            await AssertNoPlatformMoneyAsync(fleet, org, passenger);
        });

    /// <summary>
    /// §18b — subscription money never enters <c>billing.journal_*</c>, and there is no column for it.
    /// </summary>
    /// <remarks>
    /// The fence C123's brief cares most about, held two ways. The behavioural half drives all five
    /// methods against one organisation and asserts that the double-entry ledger is untouched by any
    /// of them; the structural half asks <c>information_schema</c>, because a service can be fixed
    /// and a schema cannot lie — migration 1202 gives <c>subscription.payments</c> no posting id and
    /// no commission column, which is what <c>migrate-verify.sh</c> asserts and what makes "MageRide
    /// takes no cut" a property of the data model rather than of anybody's restraint.
    /// </remarks>
    [Fact]
    public Task Subscription_money_never_enters_the_platform_ledger() =>
        RunAsync(async (fleet, parties) =>
        {
            var org = await fleet.CreateFleetOrgAsync();
            parties.AddRange(org.FleetId, org.OwnerId);

            var before = await fleet.CountPostingsAsync();

            // Four passengers, five settlements, one organisation.
            foreach (var method in new[] { "lankaqr_deeplink", "lankaqr_scan", "online_transfer" })
            {
                var passenger = await fleet.CreatePassengerAsync();
                parties.Add(passenger.Id);

                var subscription = await fleet.SubscribeAsync(org, passenger);

                using var sheet = await fleet.PaySubscriptionAsync(subscription, method);
                await MoneyFleet.AssertOkAsync(sheet, $"opening the {method} pay sheet");

                if (method == "online_transfer")
                {
                    var paymentId = (await MoneyFleet.ReadJsonAsync(sheet)).GetProperty("paymentId").GetGuid();

                    using var slip = await fleet.UploadTransferSlipAsync(paymentId, passenger);
                    await MoneyFleet.AssertOkAsync(slip, "attaching the slip");

                    using var confirmed = await MoneyFleet.PostAsync(
                        fleet.SubscriptionClient,
                        $"/v1/mode-b/payments/{paymentId}/confirm",
                        new { },
                        org.OwnerBearer);

                    await MoneyFleet.AssertOkAsync(confirmed, "the owner confirming");
                }

                await AssertNoPlatformMoneyAsync(fleet, org, passenger);
            }

            var cashPayer = await fleet.CreatePassengerAsync();
            parties.Add(cashPayer.Id);

            var cashSubscription = await fleet.SubscribeAsync(org, cashPayer);

            using (var marked = await MoneyFleet.PostAsync(
                fleet.SubscriptionClient,
                $"/v1/mode-b/{org.VehicleId}/subscribers/{cashSubscription.GrantId}/mark-cash",
                new { amountMinor = cashSubscription.MonthlyFareMinor },
                org.OwnerBearer))
            {
                await MoneyFleet.AssertOkAsync(marked, "the owner marking cash received");
            }

            // Not one posting, on any account, for any of it.
            Assert.Equal(before, await fleet.CountPostingsAsync());

            // And the structural half: there is no column a posting id could go in.
            await using var connection = await fleet.OpenAsync();

            var columns = (await connection.QueryAsync<string>(
                """
                SELECT column_name FROM information_schema.columns
                 WHERE table_schema = 'subscription' AND table_name = 'payments';
                """)).ToArray();

            Assert.DoesNotContain(columns, column =>
                column.Contains("journal", StringComparison.OrdinalIgnoreCase)
                || column.Contains("posting", StringComparison.OrdinalIgnoreCase)
                || column.Contains("entry", StringComparison.OrdinalIgnoreCase)
                || column.Contains("commission", StringComparison.OrdinalIgnoreCase));
        });

    /// <summary>
    /// The pass-through fence, checked after every settlement in this file.
    /// </summary>
    /// <remarks>
    /// Three parties and none of them may acquire a ledger account from a subscription payment: not
    /// the passenger who paid, not the owner who was paid, and not the organisation — whose account
    /// exists for exactly one thing, the platform's own per-Mode-B-vehicle charge (C060), which is a
    /// different flow that never nets against this one.
    /// </remarks>
    private static async Task AssertNoPlatformMoneyAsync(
        MoneyFleet fleet, PaidFleetOrg org, Passenger passenger)
    {
        Assert.Null(await fleet.ReadAccountAsync("passenger", passenger.Id));
        Assert.Null(await fleet.ReadAccountAsync("driver", org.OwnerId));
        Assert.Null(await fleet.ReadAccountAsync("fleet", org.FleetId));
    }

    private const System.Text.Json.JsonValueKind JsonNull = System.Text.Json.JsonValueKind.Null;
}
