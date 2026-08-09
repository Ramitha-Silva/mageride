using System.Net;
using Dapper;
using MageRide.E2E.Infrastructure;
using MageRide.TestKit;

namespace MageRide.E2E.Scenarios;

/// <summary>
/// D5' §9 — the two ways money enters a wallet, the voucher discount, and the credit transfer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every rupee here arrives through a rail the platform has, and there are exactly two of them.</b>
/// AL-05 removed bank transfer as a top-up method outright, so what is left is an OnePay session and
/// AL-15's LankaQR bank-app hand-off — plus bulk vouchers, which are not a rail at all but a purchase
/// against a balance. Both rails share the property this scenario is mostly about: <b>the wallet is
/// credited on the callback and never on the initiate</b>, because a session the gateway accepted has
/// moved no money and treating it as a credit is how a balance grows by abandoning a payment page.
/// </para>
/// <para>
/// <b>The acquirer is a real socket</b> (<see cref="AcquirerGateway"/>) speaking D6' §7.1's
/// create-session shape and signing its callbacks with the deployment's own secret, exactly as
/// C122's <c>SmsGateway</c> speaks Notify.lk's. It decides nothing: whether a callback is a first
/// delivery, a redelivery, a second transaction for one session or an amount that disagrees with it
/// is what each test chooses, because those four are four distinct R-19 behaviours.
/// </para>
/// </remarks>
[Collection<MoneyCollection>]
[Trait("Category", "Money")]
public sealed class WalletMoneyScenario(PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
    : MoneyScenario(postgres, redis, redpanda)
{
    /// <summary>US-9.18 / D6' §7.1 — the card rail, and the fact that the session credits nothing.</summary>
    [Fact]
    public Task An_OnePay_topup_credits_the_wallet_only_when_the_signed_callback_arrives() =>
        RunAsync(async (fleet, parties) =>
        {
            var driver = await fleet.CreateDriverAsync();
            parties.Add(driver.DriverId);

            var platformBefore = (await fleet.ReadPlatformAccountAsync()).BalanceMinor;
            var topup = await fleet.StartTopUpAsync(driver, 200_000);

            Assert.Equal("Pending", topup.State);

            // The session exists at the acquirer, with the order reference wallet-svc minted before
            // it called — which is what lets a callback that echoes only `orderId` find it again.
            var session = fleet.Acquirer.SessionFor(topup.OrderId);

            Assert.Equal(200_000, session.AmountMinor);
            Assert.Equal("LKR", session.Currency);

            // Nothing has moved. The driver has an account only because the initiate resolves one —
            // "the account is what the credit posts to, and looking it up twice is how a top-up ends
            // up in the wrong wallet" — and its balance is zero.
            Assert.Equal(0, await fleet.BalanceOfAsync(driver.DriverId));
            Assert.Null(await fleet.ReadEntryAsync($"topup:{topup.TopupId}"));

            var reference = $"onepay-{Guid.NewGuid():N}";

            using (var callback = await fleet.ConfirmTopUpAsync(topup, reference))
            {
                await MoneyFleet.AssertOkAsync(callback, "the acquirer confirming the session");
            }

            await fleet.UntilAsync(
                async () => (await fleet.ReadTopupAsync(topup.TopupId)).State == "Succeeded",
                $"top-up {topup.TopupId} settling");

            var settled = await fleet.ReadTopupAsync(topup.TopupId);

            Assert.Equal(reference, settled.ProviderTransactionId);
            Assert.NotNull(settled.EntryId);

            Assert.Equal(200_000, await fleet.BalanceOfAsync(driver.DriverId));

            var entry = await fleet.ReadEntryAsync($"topup:{topup.TopupId}");

            Assert.True(entry is not null, "The ledger key is topup:{topupId} — 1107's header fixes the spelling.");
            Assert.Equal("topup", entry!.Kind);
            Assert.Equal(0, entry.SumMinor);

            // Double entry: the platform's own account carries the other side of every credit it
            // hands out. The gateway settling into MageRide's bank is a fact about a bank account,
            // not about this ledger.
            var account = (await fleet.ReadAccountAsync("driver", driver.DriverId))!;

            Assert.Equal(200_000, entry.For(account.AccountId)!.AmountMinor);
            Assert.Equal(platformBefore - 200_000, (await fleet.ReadPlatformAccountAsync()).BalanceMinor);

            // The mirror and the driver's own statement line, both written inside the posting
            // transaction — five things that have to happen together.
            Assert.Equal(200_000, account.MirrorMinor);

            var statement = await fleet.ReadStatementAsync("driver", driver.DriverId);
            Assert.Equal(("topup", 200_000L, 200_000L), statement.Single());

            // D-08's cache, written through after the commit. dispatch-svc's gate reads this key.
            Assert.Equal("200000", await fleet.ReadWalletCacheAsync(driver.DriverId));
        });

    /// <summary>
    /// R-19 — a replayed gateway webhook never double-credits, and still answers <c>200</c>.
    /// </summary>
    /// <remarks>
    /// Both halves matter and they pull against each other. The credit must happen once
    /// (<c>ux_topups_provider_txn</c> catches the redelivery), and the response must stay <c>200</c>
    /// — a callback endpoint that answers an error to a redelivery is a callback endpoint the
    /// provider retries for ever.
    /// </remarks>
    [Fact]
    public Task A_replayed_gateway_webhook_never_double_credits() =>
        RunAsync(async (fleet, parties) =>
        {
            var driver = await fleet.CreateDriverAsync();
            parties.Add(driver.DriverId);

            var topup = await fleet.StartTopUpAsync(driver, 100_000);
            var reference = $"onepay-{Guid.NewGuid():N}";

            for (var delivery = 1; delivery <= 3; delivery++)
            {
                using var callback = await fleet.ConfirmTopUpAsync(topup, reference);

                await MoneyFleet.AssertOkAsync(callback, $"delivery {delivery} of the same gateway transaction");

                Assert.True(
                    (await MoneyFleet.ReadJsonAsync(callback)).GetProperty("received").GetBoolean(),
                    "A redelivery is answered with the same body as the first delivery — anything else "
                    + "makes the provider retry for ever.");

                await fleet.UntilAsync(
                    async () => (await fleet.ReadTopupAsync(topup.TopupId)).State == "Succeeded",
                    "the first delivery settling");
            }

            Assert.Equal(100_000, await fleet.BalanceOfAsync(driver.DriverId));

            var topups = await fleet.ReadEntriesForAsync("driver", driver.DriverId, "topup");

            Assert.True(
                topups.Count == 1,
                $"Three deliveries of one gateway transaction produced {topups.Count} journal entries: "
                + string.Join(", ", topups.Select(entry => entry.IdempotencyKey)));

            Assert.Single(await fleet.ReadStatementAsync("driver", driver.DriverId));
        });

    /// <summary>
    /// The second R-19 guard: two <b>different</b> transactions for one session credit once.
    /// </summary>
    /// <remarks>
    /// <c>ux_topups_provider_txn</c> catches a redelivery of the same gateway transaction; this is
    /// the other shape — a provider retrying under a fresh transaction id, which that index cannot
    /// see. What stops it is the session's own state: a <c>Succeeded</c> top-up is not a session
    /// anything else may settle, and the ledger's <c>topup:{topupId}</c> key stands behind that.
    /// </remarks>
    [Fact]
    public Task A_second_gateway_transaction_for_one_session_credits_nothing_more() =>
        RunAsync(async (fleet, parties) =>
        {
            var driver = await fleet.CreateDriverAsync();
            parties.Add(driver.DriverId);

            var topup = await fleet.TopUpAsync(driver, 100_000, $"onepay-{Guid.NewGuid():N}");

            using var second = await fleet.ConfirmTopUpAsync(topup, $"onepay-retry-{Guid.NewGuid():N}");

            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            Assert.Equal(100_000, await fleet.BalanceOfAsync(driver.DriverId));
            Assert.Single(await fleet.ReadEntriesForAsync("driver", driver.DriverId, "topup"));
        });

    /// <summary>
    /// AL-15 / D6' §7.2 — the LankaQR rail, its own secret, and the bank-app deep link.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two rails have <b>different secrets</b>, and honouring a OnePay-signed body on the LankaQR
    /// route would let either secret settle the other's money. So the cross-rail refusal is asserted
    /// here rather than assumed: it is the one failure that would be invisible in production until
    /// somebody's key leaked.
    /// </para>
    /// <para>
    /// <c>qrPayload</c> is absent, and its absence is the deployment's decision rather than an
    /// oversight: a LankaQR payload is an EMVCo TLV string whose merchant fields and CRC belong to
    /// the acquiring bank, so composing one would put a plausible, unscannable code in front of a
    /// driver. <c>LankaQr:PayloadTemplate</c> is deliberately unset in this fleet.
    /// </para>
    /// </remarks>
    [Fact]
    public Task A_LankaQR_topup_hands_off_to_the_bank_app_and_settles_on_its_own_secret() =>
        RunAsync(async (fleet, parties) =>
        {
            var driver = await fleet.CreateDriverAsync();
            parties.Add(driver.DriverId);

            using (var opened = await MoneyFleet.PostAsync(
                fleet.WalletClient, "/v1/wallet/topup/lankaqr", new { amountMinor = 300_000 }, driver.Bearer))
            {
                await MoneyFleet.AssertOkAsync(opened, "opening a LankaQR top-up");

                var body = await MoneyFleet.ReadJsonAsync(opened);

                Assert.Equal("Pending", body.GetProperty("state").GetString());

                var link = body.GetProperty("paymentLink").GetString();

                Assert.StartsWith("combank://pay?ref=", link, StringComparison.Ordinal);
                Assert.Contains("amount=300000", link, StringComparison.Ordinal);

                Assert.False(
                    body.TryGetProperty("qrPayload", out _),
                    "LankaQr:PayloadTemplate is unset on this deployment, so the AL-15 QR fallback is "
                    + "omitted rather than invented. An EMVCo payload belongs to the acquiring bank.");
            }

            var topup = await fleet.ReadLatestTopupAsync(driver.DriverId);

            Assert.Equal("lankaqr", topup.Method);

            // The OnePay secret on the LankaQR route: a valid HMAC under the wrong key.
            using (var wrongRail = await fleet.Acquirer.ConfirmAsync(
                fleet.TopupCallbackUrl("lankaqr"),
                MoneyFleet.OnepayWebhookSecret,
                new
                {
                    providerTransactionId = $"lankaqr-{Guid.NewGuid():N}",
                    topupId = topup.TopupId.ToString(),
                    orderId = topup.OrderId,
                    status = "SUCCESS",
                    amountMinor = topup.AmountMinor,
                    currency = "LKR",
                }))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, wrongRail.StatusCode);
            }

            Assert.Equal(0, await fleet.BalanceOfAsync(driver.DriverId));

            using (var settled = await fleet.ConfirmTopUpAsync(
                topup, $"lankaqr-{Guid.NewGuid():N}", method: "lankaqr"))
            {
                await MoneyFleet.AssertOkAsync(settled, "the bank confirming a LankaQR transfer");
            }

            await fleet.UntilAsync(
                async () => await fleet.BalanceOfAsync(driver.DriverId) == 300_000,
                "the LankaQR top-up crediting the driver's wallet");

            Assert.Equal("topup", (await fleet.ReadEntryAsync($"topup:{topup.TopupId}"))!.Kind);
        });

    /// <summary>
    /// A wallet-credit endpoint that trusts an unsigned body is a free-money endpoint.
    /// </summary>
    /// <remarks>
    /// There is no "accept unsigned" mode anywhere on this surface, and the two shapes are tested
    /// separately because they fail at different points: a missing header never reaches the
    /// comparison, and a well-formed digest under the wrong key does.
    /// </remarks>
    [Fact]
    public Task An_unsigned_or_wrongly_signed_callback_credits_nothing() =>
        RunAsync(async (fleet, parties) =>
        {
            var driver = await fleet.CreateDriverAsync();
            parties.Add(driver.DriverId);

            var topup = await fleet.StartTopUpAsync(driver, 100_000);

            var body = new
            {
                providerTransactionId = $"forged-{Guid.NewGuid():N}",
                topupId = topup.TopupId.ToString(),
                orderId = topup.OrderId,
                status = "SUCCESS",
                amountMinor = topup.AmountMinor,
                currency = "LKR",
            };

            using (var unsigned = await fleet.Acquirer.ConfirmUnsignedAsync(
                fleet.TopupCallbackUrl("onepay"), body))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
            }

            using (var forged = await fleet.Acquirer.ConfirmWithWrongSecretAsync(
                fleet.TopupCallbackUrl("onepay"), body))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
            }

            Assert.Equal(0, await fleet.BalanceOfAsync(driver.DriverId));
            Assert.Equal("Pending", (await fleet.ReadTopupAsync(topup.TopupId)).State);
            Assert.Null(await fleet.ReadEntryAsync($"topup:{topup.TopupId}"));
        });

    /// <summary>
    /// D6' §7.2 — a callback whose amount disagrees with its session credits nothing, either way.
    /// </summary>
    /// <remarks>
    /// Crediting what the callback says would let a misconfigured or spoofed provider set the
    /// balance; crediting what the session says would credit money the driver may not have paid.
    /// Both are wrong, so the session stays <c>Pending</c> and the mismatch is a settlement exception
    /// for Finance rather than a number to pick.
    /// </remarks>
    [Fact]
    public Task A_callback_whose_amount_disagrees_with_its_session_credits_nothing() =>
        RunAsync(async (fleet, parties) =>
        {
            var driver = await fleet.CreateDriverAsync();
            parties.Add(driver.DriverId);

            var topup = await fleet.StartTopUpAsync(driver, 100_000);

            using (var mismatch = await fleet.ConfirmTopUpAsync(
                topup, $"onepay-{Guid.NewGuid():N}", amountMinor: 900_000))
            {
                Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
            }

            Assert.Equal(0, await fleet.BalanceOfAsync(driver.DriverId));
            Assert.Equal("Pending", (await fleet.ReadTopupAsync(topup.TopupId)).State);

            // The session is still open, so the driver's real payment can still settle it.
            using (var honest = await fleet.ConfirmTopUpAsync(topup, $"onepay-{Guid.NewGuid():N}"))
            {
                await MoneyFleet.AssertOkAsync(honest, "the correctly-valued callback that follows");
            }

            await fleet.UntilAsync(
                async () => await fleet.BalanceOfAsync(driver.DriverId) == 100_000,
                "the corrected callback crediting the session's own amount");
        });

    /// <summary>
    /// US-9.19 / AL-01 — a voucher is paid for at a discount and credited at face value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// D5' §9.3's worked example, which is also ADD §9.1's: a 10 % voucher on Rs 1,000 means pay
    /// Rs 900 and receive Rs 1,000. <b>The discount reduces the price and never the credit</b> —
    /// <c>ck_voucher_purchases_credited</c> makes that a database constraint — and the ledger entry
    /// moves the <em>face value</em> both ways, because what the buyer paid is a fact about a gateway
    /// settlement rather than about this ledger.
    /// </para>
    /// <para>
    /// That gap is the whole of an informal reseller's margin (AL-01): they buy at 900 and pass
    /// credit on at par, because a driver-to-driver transfer moves the exact value and there is no
    /// journal kind that could record a commission.
    /// </para>
    /// </remarks>
    [Fact]
    public Task A_bulk_voucher_is_paid_for_at_a_discount_and_credited_at_face_value() =>
        RunAsync(async (fleet, parties) =>
        {
            var driver = await fleet.CreateDriverAsync();
            parties.Add(driver.DriverId);

            long denomination;
            int discountBps;

            using (var tiers = await MoneyFleet.GetAsync(
                fleet.WalletClient, "/v1/wallet/voucher/discount-tiers", driver.Bearer))
            {
                await MoneyFleet.AssertOkAsync(tiers, "reading the bulk-voucher ladder");

                // The rung ADD §9.1 and US-9.19 both work through: Rs 1,000 at 10 %.
                var rung = (await MoneyFleet.ReadJsonAsync(tiers)).GetProperty("tiers")
                    .EnumerateArray()
                    .Single(tier => tier.GetProperty("denominationMinor").GetInt64() == 100_000);

                denomination = rung.GetProperty("denominationMinor").GetInt64();
                discountBps = rung.GetProperty("discountBps").GetInt32();

                Assert.Equal(1000, discountBps);
            }

            var platformBefore = (await fleet.ReadPlatformAccountAsync()).BalanceMinor;

            using (var purchase = await MoneyFleet.PostAsync(
                fleet.WalletClient,
                "/v1/wallet/voucher/purchase",
                new { denominationMinor = denomination, gatewayRef = $"voucher-{Guid.NewGuid():N}" },
                driver.Bearer))
            {
                await MoneyFleet.AssertStatusAsync(purchase, HttpStatusCode.Created, "buying a bulk voucher");

                var body = await MoneyFleet.ReadJsonAsync(purchase);

                Assert.Equal(90_000, body.GetProperty("paidMinor").GetInt64());
                Assert.Equal(100_000, body.GetProperty("creditedMinor").GetInt64());
                Assert.Equal(1000, body.GetProperty("discountBps").GetInt32());
                Assert.Equal(100_000, body.GetProperty("balanceAfterMinor").GetInt64());
            }

            Assert.Equal(100_000, await fleet.BalanceOfAsync(driver.DriverId));

            var entries = await fleet.ReadEntriesForAsync("driver", driver.DriverId, "voucher_purchase");
            var entry = Assert.Single(entries);

            Assert.Equal(0, entry.SumMinor);
            Assert.StartsWith("voucher_purchase:", entry.IdempotencyKey, StringComparison.Ordinal);

            var account = (await fleet.ReadAccountAsync("driver", driver.DriverId))!;

            Assert.Equal(100_000, entry.For(account.AccountId)!.AmountMinor);

            // The face value moved both ways. The Rs 100 discount is the platform's cost of the sale
            // and is recorded on the purchase row, not as a third leg — there is no journal kind for
            // one, which is what makes AL-01's "no commission" structural.
            Assert.Equal(platformBefore - 100_000, (await fleet.ReadPlatformAccountAsync()).BalanceMinor);
            Assert.Equal(2, entry.Legs.Count);
        });

    /// <summary>
    /// US-9.10/9.13 — a credit request, and the exact value moving when the holder approves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// AL-01's whole claim in one entry: two legs of equal and opposite value, <b>no third leg</b>,
    /// and the platform account not a party to it at all. That is not a policy in code that could be
    /// edited — <c>ck_journal_entries_kind</c> has no value a commission could be recorded under —
    /// so the assertion that the entry has exactly two legs is asserting a fence the database holds.
    /// </para>
    /// <para>
    /// The balance is checked at <em>approval</em> and not at request: what the holder can afford
    /// when they answer is the only figure that matters, and this scenario tops the holder up between
    /// the two to prove it.
    /// </para>
    /// </remarks>
    [Fact]
    public Task A_credit_request_moves_the_exact_amount_when_the_holder_approves() =>
        RunAsync(async (fleet, parties) =>
        {
            var holder = await fleet.CreateDriverAsync();
            var requester = await fleet.CreateDriverAsync();
            parties.AddRange(holder.DriverId, requester.DriverId);

            Guid transferId;

            // Requested before the holder has a rupee: nothing is checked and nothing moves.
            using (var requested = await MoneyFleet.PostAsync(
                fleet.WalletClient,
                "/v1/wallet/credit-transfer/request",
                new { holderDriverId = holder.DriverId.ToString(), amountMinor = 150_000 },
                requester.Bearer))
            {
                await MoneyFleet.AssertStatusAsync(
                    requested, HttpStatusCode.Created, "requesting credit from another driver");

                var body = await MoneyFleet.ReadJsonAsync(requested);

                transferId = body.GetProperty("transferId").GetGuid();
                Assert.Equal("PENDING", body.GetProperty("status").GetString());
            }

            Assert.Equal(0, await fleet.BalanceOfAsync(holder.DriverId));
            Assert.Equal(0, await fleet.BalanceOfAsync(requester.DriverId));

            using (var pending = await MoneyFleet.GetAsync(
                fleet.WalletClient, "/v1/wallet/credit-transfer/pending", holder.Bearer))
            {
                await MoneyFleet.AssertOkAsync(pending, "the holder's pending credit requests");

                Assert.Contains(
                    (await MoneyFleet.ReadJsonAsync(pending)).GetProperty("items").EnumerateArray(),
                    item => item.GetProperty("transferId").GetGuid() == transferId);
            }

            await fleet.TopUpAsync(holder, 500_000, $"onepay-{Guid.NewGuid():N}");

            using (var approved = await MoneyFleet.PostAsync(
                fleet.WalletClient,
                $"/v1/wallet/credit-transfer/{transferId}/approve",
                new { },
                holder.Bearer))
            {
                await MoneyFleet.AssertOkAsync(approved, "the holder approving the request");
                Assert.Equal("APPROVED", (await MoneyFleet.ReadJsonAsync(approved)).GetProperty("status").GetString());
            }

            Assert.Equal(350_000, await fleet.BalanceOfAsync(holder.DriverId));
            Assert.Equal(150_000, await fleet.BalanceOfAsync(requester.DriverId));

            var entry = Assert.Single(
                await fleet.ReadEntriesForAsync("driver", requester.DriverId, "driver_transfer"));

            Assert.Equal(0, entry.SumMinor);
            Assert.Equal($"driver_transfer:{transferId}", entry.IdempotencyKey);

            Assert.True(
                entry.Legs.Count == 2,
                "AL-01: a transfer is two legs of equal and opposite value and there is no commission. "
                + $"This entry has {entry.Legs.Count} legs: "
                + string.Join(", ", entry.Legs.Select(leg => $"{leg.OwnerType} {leg.AmountMinor}")));

            Assert.DoesNotContain(entry.Legs, leg => leg.OwnerType is "platform" or "suspense");

            // A second tap on Approve is a conflict, not a second movement: the PENDING predicate is
            // the claim, and it lives inside the ledger's own transaction.
            using (var again = await MoneyFleet.PostAsync(
                fleet.WalletClient,
                $"/v1/wallet/credit-transfer/{transferId}/approve",
                new { },
                holder.Bearer))
            {
                Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            }

            Assert.Equal(350_000, await fleet.BalanceOfAsync(holder.DriverId));
        });

    /// <summary>US-9A.12 — a proactive send moves the exact value on the spot, by Driver ID (AL-34).</summary>
    [Fact]
    public Task A_direct_send_moves_the_exact_amount_by_driver_id() =>
        RunAsync(async (fleet, parties) =>
        {
            var sender = await fleet.CreateDriverAsync();
            var recipient = await fleet.CreateDriverAsync();
            parties.AddRange(sender.DriverId, recipient.DriverId);

            await fleet.TopUpAsync(sender, 200_000, $"onepay-{Guid.NewGuid():N}");

            using (var sent = await MoneyFleet.PostAsync(
                fleet.WalletClient,
                "/v1/wallet/credit-transfer/initiate",
                new { recipientDriverId = recipient.DriverId.ToString(), amountMinor = 75_000 },
                sender.Bearer))
            {
                await MoneyFleet.AssertStatusAsync(sent, HttpStatusCode.Created, "sending credit to another driver");
                Assert.Equal("APPROVED", (await MoneyFleet.ReadJsonAsync(sent)).GetProperty("status").GetString());
            }

            Assert.Equal(125_000, await fleet.BalanceOfAsync(sender.DriverId));
            Assert.Equal(75_000, await fleet.BalanceOfAsync(recipient.DriverId));

            // Exactly par in both directions. The sender's margin, if they have one, came from the
            // voucher discount at purchase and from nowhere else.
            var entry = Assert.Single(
                await fleet.ReadEntriesForAsync("driver", recipient.DriverId, "driver_transfer"));

            Assert.Equal(2, entry.Legs.Count);
            Assert.Equal(75_000, entry.Legs.Max(leg => leg.AmountMinor));
            Assert.Equal(-75_000, entry.Legs.Min(leg => leg.AmountMinor));

            // A driver may not send what they do not have: §10 leaves non-negativity to the
            // application and 402 insufficient-wallet is where it lives.
            using var overdrawn = await MoneyFleet.PostAsync(
                fleet.WalletClient,
                "/v1/wallet/credit-transfer/initiate",
                new { recipientDriverId = recipient.DriverId.ToString(), amountMinor = 500_000 },
                sender.Bearer);

            Assert.Equal(HttpStatusCode.PaymentRequired, overdrawn.StatusCode);
            Assert.Equal("insufficient-wallet", await MoneyFleet.ProblemCodeAsync(overdrawn));
            Assert.Equal(125_000, await fleet.BalanceOfAsync(sender.DriverId));
        });

    /// <summary>US-9.12 — the holder declines, and nothing is posted.</summary>
    [Fact]
    public Task A_rejected_credit_request_moves_nothing() =>
        RunAsync(async (fleet, parties) =>
        {
            var holder = await fleet.CreateDriverAsync();
            var requester = await fleet.CreateDriverAsync();
            parties.AddRange(holder.DriverId, requester.DriverId);

            await fleet.TopUpAsync(holder, 200_000, $"onepay-{Guid.NewGuid():N}");

            Guid transferId;

            using (var requested = await MoneyFleet.PostAsync(
                fleet.WalletClient,
                "/v1/wallet/credit-transfer/request",
                new { holderDriverId = holder.DriverId.ToString(), amountMinor = 50_000 },
                requester.Bearer))
            {
                await MoneyFleet.AssertStatusAsync(requested, HttpStatusCode.Created, "requesting credit");
                transferId = (await MoneyFleet.ReadJsonAsync(requested)).GetProperty("transferId").GetGuid();
            }

            using (var rejected = await MoneyFleet.PostAsync(
                fleet.WalletClient, $"/v1/wallet/credit-transfer/{transferId}/reject", new { }, holder.Bearer))
            {
                await MoneyFleet.AssertOkAsync(rejected, "the holder declining");
                Assert.Equal("REJECTED", (await MoneyFleet.ReadJsonAsync(rejected)).GetProperty("status").GetString());
            }

            Assert.Equal(200_000, await fleet.BalanceOfAsync(holder.DriverId));
            Assert.Equal(0, await fleet.BalanceOfAsync(requester.DriverId));
            Assert.Empty(await fleet.ReadEntriesForAsync("driver", holder.DriverId, "driver_transfer"));

            // Somebody else's credit request is a 404 rather than a 403 — the house rule, and here it
            // stops the endpoint being an oracle over other drivers' requests.
            var stranger = await fleet.CreateDriverAsync();
            parties.Add(stranger.DriverId);

            using var notTheirs = await MoneyFleet.PostAsync(
                fleet.WalletClient, $"/v1/wallet/credit-transfer/{transferId}/approve", new { }, stranger.Bearer);

            Assert.Equal(HttpStatusCode.NotFound, notTheirs.StatusCode);
        });

    /// <summary>
    /// AL-05 — bank transfer is not a top-up method, and cannot be made one by configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// C123's second fence, and the platform holds it in three independent places rather than one:
    /// there is no route to call, no <c>method</c> value the service would accept, and
    /// <c>ck_topups_method</c> (migration 1107) refuses the row outright. The last is asserted by
    /// making the database reject it, because a CHECK nobody has ever tested is a CHECK that might
    /// have been dropped in a later migration.
    /// </para>
    /// <para>
    /// The reconciliation queue AL-05 removed with it has no assertion here for the same reason it
    /// has no code: there is nothing to call.
    /// </para>
    /// </remarks>
    [Fact]
    public Task There_is_no_bank_transfer_topup_anywhere_on_this_platform() =>
        RunAsync(async (fleet, parties) =>
        {
            var driver = await fleet.CreateDriverAsync();
            parties.Add(driver.DriverId);

            // Neither spelling is a route. 405 rather than 404 on some of them is the GET
            // `/v1/wallet/topup/{topupId}` template matching the segment — which says the same
            // thing: nothing here accepts a POST that would open a bank-transfer session.
            foreach (var path in new[] { "bank-transfer", "banktransfer", "bank", "transfer" })
            {
                using var attempt = await MoneyFleet.PostAsync(
                    fleet.WalletClient, $"/v1/wallet/topup/{path}", new { amountMinor = 100_000 }, driver.Bearer);

                Assert.True(
                    attempt.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
                    $"POST /v1/wallet/topup/{path} answered {(int)attempt.StatusCode}. AL-05 removed the "
                    + "bank-transfer rail: there is no route, and the contract lists it under 'do not re-add'.");
            }

            Assert.Equal(0, await fleet.CountTopupsAsync(driver.DriverId));

            // And the row the database will not hold, whatever a service decided to write.
            await using var connection = await fleet.OpenAsync();

            var refused = await Assert.ThrowsAsync<Npgsql.PostgresException>(async () =>
                await connection.ExecuteAsync(
                    """
                    INSERT INTO billing.topups (driver_id, account_id, method, amount_minor)
                    SELECT @DriverId, a.id, 'bank_transfer', 100000
                      FROM billing.accounts a
                     WHERE a.owner_type = 'platform' AND a.owner_id IS NULL AND a.currency = 'LKR';
                    """,
                    new { DriverId = driver.DriverId }));

            Assert.Equal("23514", refused.SqlState);
            Assert.Equal("ck_topups_method", refused.ConstraintName);

            Assert.Equal(0, await fleet.BalanceOfAsync(driver.DriverId));
        });
}
