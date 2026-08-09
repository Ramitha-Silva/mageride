using Dapper;
using MageRide.E2E.Infrastructure;
using MageRide.TestKit;

namespace MageRide.E2E.Scenarios;

/// <summary>
/// The ratchet: every kind of money this platform can record is either driven by this suite or
/// accounted for, and every gap C123 found is still a gap.
/// </summary>
/// <remarks>
/// <para>
/// <c>ck_journal_entries_kind</c> is the closed vocabulary of everything that can move money on this
/// platform — twelve values across four migrations. A money suite whose coverage was a list in a
/// handoff would go stale the first time somebody added a thirteenth; this reads the CHECK itself and
/// fails if a kind appears that is neither driven here nor written down below with a reason. It is
/// C120's <c>MatrixCoverage</c> applied to the ledger rather than to the cancellation matrix, and it
/// fails <b>both ways</b> — a kind listed as unreachable that becomes reachable is as loud as one
/// nobody covered.
/// </para>
/// <para>
/// <b>The gaps below are the C123 findings.</b> Each is asserted <em>as</em> a gap by a named test
/// elsewhere in this suite rather than softened into a passing assertion here; what this file adds
/// is that the list cannot grow silently, and that a fix breaks the build rather than going
/// unnoticed.
/// </para>
/// </remarks>
[Collection<MoneyCollection>]
[Trait("Category", "Money")]
public sealed class MoneyLedgerCoverage(PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
    : MoneyScenario(postgres, redis, redpanda)
{
    /// <summary>
    /// The kinds a scenario in this suite actually posts, and where.
    /// </summary>
    /// <remarks>
    /// Every one of these is written by a service in the money fleet, over a real HTTP hop, in
    /// response to something a passenger, a driver, an owner or an acquirer did.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> Driven =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["topup"] =
                "WalletMoneyScenario — an OnePay session and an AL-15 LankaQR hand-off, each credited "
                + "by a signed provider callback and never by the initiate.",
            ["daily_fee"] =
                "DailyFeeScenario — D-13, charged before a driver's second trip of the Colombo day.",
            ["trip_payment"] =
                "RidePaymentScenario — AL-57's wallet fare: two wallet legs, no platform leg.",
            ["voucher_purchase"] =
                "WalletMoneyScenario — US-9.19's bulk voucher: paid at a discount, credited at face value.",
            ["driver_transfer"] =
                "WalletMoneyScenario — US-9.13's credit transfer, at exactly par (AL-01).",
            ["tip_payout"] =
                "RidePaymentScenario — E-10's post-trip tip, credited straight to the driver.",
            ["adjustment"] =
                "RidePaymentScenario — posted through the internal seam while proving that the seam "
                + "cannot fund a passenger wallet (it opens a driver account instead).",
        };

    /// <summary>
    /// The kinds this suite does not post, each with the reason and the component that owns it.
    /// </summary>
    /// <remarks>
    /// Two shapes in here and they are different. <c>penalty_settle</c>, <c>fleet_invoice</c> and
    /// <c>driver_payout</c> are <b>somebody else's component</b> — reachable on this platform, driven
    /// by the suite that owns them. <c>payment_refund</c> and <c>overpaid_reversal</c> are
    /// <b>defects</b>: E-05's reversal is refused by wallet-svc's own whitelist, and R-19's late
    /// callback has no door left to arrive through. Both are asserted as such by name.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> Accounted =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["penalty_settle"] =
                "D-05's cross-trip cancellation settlement is C120's CancellationMatrixScenario, which "
                + "drives the penalty itself; its ledger leg needs Fare:WalletBaseUrl, which that fleet "
                + "deliberately leaves unset. It is reachable in this fleet — a candidate for the next "
                + "pass rather than a gap in the platform.",
            ["fleet_invoice"] =
                "The consolidated monthly per-Mode-B-vehicle charge is fleet-billing-svc's (C060), and "
                + "that service is not in this fleet. Subscription-svc's own per-vehicle DUE rows are "
                + "off here (Subscription:ModeBBillingEnabled=false) because its hourly runner sweeps "
                + "every Mode B vehicle in a database this suite shares with C121's.",
            ["driver_payout"] =
                "AL-58's weekly sweep is payout-svc's (C061), which is not in this fleet. It is the "
                + "entry that discharges what AL-57 creates, and it belongs with the run that raises "
                + "the instruction beside it.",
            ["payment_refund"] =
                "**A defect.** RefundService posts the reversal through the internal *debit* route "
                + "with kind 'payment_refund', which is in wallet-svc's InternalCreditKinds and not "
                + "its debit whitelist — so wallet-svc answers 400, fare-svc answers 503, and the "
                + "refund row and the payment's transition have already committed. Asserted by "
                + "RidePaymentScenario.A_refund_raises_the_finance_queue_row_and_its_ledger_leg_cannot_post.",
            ["overpaid_reversal"] =
                "**Unreachable.** R-19's late gateway callback is the only thing that produces an "
                + "Overpaid payment, and AL-57/AL-59 removed both ride-side provider callbacks with "
                + "the ride gateways. Asserted by RidePaymentScenario."
                + "A_late_gateway_callback_cannot_reach_a_settled_cash_fare_because_no_ride_rail_has_one.",
        };

    /// <summary>
    /// Every kind in <c>ck_journal_entries_kind</c> is driven or accounted for, and no kind is both.
    /// </summary>
    /// <remarks>
    /// Read off the constraint rather than off a constant, so the day a migration adds a thirteenth
    /// kind this fails and somebody has to say which of the two lists it belongs in. A kind in both
    /// lists is an error too: it means a reason survived the work that made it wrong.
    /// </remarks>
    [Fact]
    public Task Every_kind_of_money_this_platform_records_is_driven_or_accounted_for() =>
        RunAsync(async (fleet, _) =>
        {
            var kinds = await ReadKindVocabularyAsync(fleet);

            Assert.True(
                kinds.Count >= 12,
                $"ck_journal_entries_kind was read as {kinds.Count} values, which is fewer than the twelve "
                + "migrations 1101, 1108 and 1109 declare between them. The parse below is what went wrong, "
                + "not the platform.");

            var uncovered = kinds
                .Where(kind => !Driven.ContainsKey(kind) && !Accounted.ContainsKey(kind))
                .ToArray();

            Assert.True(
                uncovered.Length == 0,
                "These journal kinds can move money on this platform and this suite neither drives them nor "
                + "says why not. Add a scenario, or add an entry to MoneyLedgerCoverage.Accounted with the "
                + $"component that owns it: {string.Join(", ", uncovered)}");

            var both = Driven.Keys.Intersect(Accounted.Keys, StringComparer.Ordinal).ToArray();

            Assert.True(
                both.Length == 0,
                $"Listed as both driven and accounted for: {string.Join(", ", both)}. One of the two "
                + "entries is describing work that has since been done.");

            var invented = Driven.Keys.Concat(Accounted.Keys)
                .Where(kind => !kinds.Contains(kind))
                .ToArray();

            Assert.True(
                invented.Length == 0,
                $"Named in this suite's coverage and not in ck_journal_entries_kind: {string.Join(", ", invented)}. "
                + "Either the kind was removed by a migration, or it was never spelled the way the CHECK "
                + "spells it — and an entry keyed on a value the database will not accept covers nothing.");
        });

    /// <summary>
    /// The kinds this suite claims to drive are actually posted somewhere in <c>billing</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The half that stops the list above becoming aspirational. Every entry in <see cref="Driven"/>
    /// names a scenario, and this asserts that a run of this suite really does leave an entry of that
    /// kind behind — so a scenario that is deleted, renamed away or quietly stops reaching its
    /// posting is caught here rather than by nobody.
    /// </para>
    /// <para>
    /// <b>Test order is not relied on.</b> xUnit runs this collection one class at a time in an order
    /// it chooses, so this drives one of each kind itself rather than assuming the other scenarios
    /// have already run — which also makes it a standalone smoke test of the whole money fleet.
    /// </para>
    /// </remarks>
    [Fact]
    public Task Every_kind_this_suite_claims_to_drive_is_one_it_can_actually_post() =>
        RunAsync(async (fleet, parties) =>
        {
            var driver = await fleet.CreateDriverAsync();
            var recipient = await fleet.CreateDriverAsync();
            var passenger = await fleet.CreatePassengerAsync();
            parties.AddRange(driver.DriverId, recipient.DriverId, passenger.Id);

            // topup — a real session, a real acquirer, a signed callback.
            await fleet.TopUpAsync(driver, 500_000, $"coverage-{Guid.NewGuid():N}");

            // voucher_purchase — the Rs 1,000 rung at 10 %.
            using (var voucher = await MoneyFleet.PostAsync(
                fleet.WalletClient,
                "/v1/wallet/voucher/purchase",
                new { denominationMinor = 100_000, gatewayRef = $"coverage-{Guid.NewGuid():N}" },
                driver.Bearer))
            {
                await MoneyFleet.AssertStatusAsync(
                    voucher, System.Net.HttpStatusCode.Created, "buying a voucher");
            }

            // driver_transfer — exactly par, no fee leg.
            using (var sent = await MoneyFleet.PostAsync(
                fleet.WalletClient,
                "/v1/wallet/credit-transfer/initiate",
                new { recipientDriverId = recipient.DriverId.ToString(), amountMinor = 10_000 },
                driver.Bearer))
            {
                await MoneyFleet.AssertStatusAsync(sent, System.Net.HttpStatusCode.Created, "sending credit");
            }

            // daily_fee — first trip free, second charged.
            var first = await fleet.StartTripAsync(driver);
            await fleet.ChargeDailyFeeAsync(driver, first.RideId);
            await fleet.FinishTripAsync(first);

            var second = await fleet.StartTripAsync(driver);
            await fleet.ChargeDailyFeeAsync(driver, second.RideId);

            // tip_payout — E-10, on the cash settlement of that same trip.
            await fleet.CompleteAndPriceAsync(second);

            using (var paid = await fleet.PayAsync(second.RideId, second.Passenger, "cash", tipMinor: 5_000))
            {
                await MoneyFleet.AssertOkAsync(paid, "paying with a tip");
            }

            // adjustment — admin-bff's correction kind, on the seam it uses.
            using (var adjusted = await fleet.PostLedgerAsync(
                driver.DriverId,
                "credit",
                1_000,
                "adjustment",
                $"adjustment:coverage-{Guid.NewGuid():N}",
                "A Finance correction (US-14.11)"))
            {
                await MoneyFleet.AssertOkAsync(adjusted, "posting an adjustment");
            }

            // trip_payment — AL-57's wallet fare, on a ride of its own.
            await fleet.OpenPassengerBalanceAsync(passenger, 500_000);

            var walletRide = await fleet.PayableRideAsync(passenger, await fleet.CreateDriverAsync());

            using (var paid = await fleet.PayAsync(walletRide.Ride.RideId, passenger, "wallet"))
            {
                await MoneyFleet.AssertOkAsync(paid, "paying a fare from the wallet");
            }

            await fleet.UntilAsync(
                async () => (await ReadPostedKindsAsync(fleet)).IsSupersetOf(Driven.Keys),
                "every kind this suite claims to drive appearing in billing.journal_entries");

            var posted = await ReadPostedKindsAsync(fleet);
            var missing = Driven.Keys.Where(kind => !posted.Contains(kind)).ToArray();

            Assert.True(
                missing.Length == 0,
                "MoneyLedgerCoverage.Driven claims these kinds are posted by this suite and a full run "
                + $"leaves none behind: {string.Join(", ", missing)}");
        });

    /// <summary>The <c>kind</c> values <c>ck_journal_entries_kind</c> admits, read from the constraint.</summary>
    private static async Task<IReadOnlySet<string>> ReadKindVocabularyAsync(MoneyFleet fleet)
    {
        await using var connection = await fleet.OpenAsync();

        var definition = await connection.ExecuteScalarAsync<string>(
            """
            SELECT pg_get_constraintdef(c.oid)
              FROM pg_constraint c
              JOIN pg_class t ON t.oid = c.conrelid
              JOIN pg_namespace n ON n.oid = t.relnamespace
             WHERE n.nspname = 'billing' AND t.relname = 'journal_entries'
               AND c.conname = 'ck_journal_entries_kind';
            """);

        Assert.True(
            definition is not null,
            "billing.journal_entries has no ck_journal_entries_kind. That CHECK is what makes 'there is no "
            + "reseller commission' (AL-01) a property of the database rather than of anybody's restraint.");

        // The definition renders as CHECK ((kind = ANY (ARRAY['topup'::text, …])). The quoted
        // literals are the vocabulary; nothing else in the string is quoted.
        return System.Text.RegularExpressions.Regex
            .Matches(definition!, "'(?<kind>[a-z_]+)'::text")
            .Select(match => match.Groups["kind"].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Every kind that has ever been posted on this database.</summary>
    private static async Task<IReadOnlySet<string>> ReadPostedKindsAsync(MoneyFleet fleet)
    {
        await using var connection = await fleet.OpenAsync();

        return (await connection.QueryAsync<string>(
            "SELECT DISTINCT kind FROM billing.journal_entries;")).ToHashSet(StringComparer.Ordinal);
    }
}
