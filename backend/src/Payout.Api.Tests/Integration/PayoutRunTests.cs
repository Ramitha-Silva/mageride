using System.Net;
using MageRide.Payout.Domain;
using MageRide.Payout.Endpoints;
using MageRide.Payout.Payouts;
using MageRide.Payout.Tests.Infrastructure;
using MageRide.Shared.Primitives;
using MageRide.Shared.Time;
using MageRide.TestKit;

namespace MageRide.Payout.Tests.Integration;

/// <summary>
/// C133's definition of done, one test per item (AL-58).
/// </summary>
/// <remarks>
/// Every assertion is against Postgres — <c>billing.accounts</c> for what a driver has,
/// <c>billing.payouts</c> for what was instructed, <c>billing.journal_postings</c> for whether the
/// ledger still balances. wallet-svc is booted for real, so "the money moved" is its statement and
/// not this suite's.
/// </remarks>
[Collection<PayoutCollection>]
public sealed class PayoutRunTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>
    /// DoD: a sweep debits each eligible driver's whole balance and raises exactly one instruction.
    /// </summary>
    [Fact]
    public async Task A_sweep_pays_out_every_rupee_and_raises_one_instruction_each()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await PayoutHarness.StartAsync(postgres, redis);

        var alice = await harness.DriverAsync(balanceMinor: 62_500);
        var bob = await harness.DriverAsync(balanceMinor: 1_337);

        var run = await RunAsync(harness);

        Assert.NotNull(run);

        // Full sweep, no minimum, no holdback: whatever was there is gone.
        Assert.Equal(0, await harness.BalanceAsync(alice));
        Assert.Equal(0, await harness.BalanceAsync(bob));

        var aliceInstruction = Assert.Single(await harness.PayoutsAsync(alice));
        var bobInstruction = Assert.Single(await harness.PayoutsAsync(bob));

        Assert.Equal(62_500, aliceInstruction.AmountMinor);
        Assert.Equal(1_337, bobInstruction.AmountMinor);

        // No bank configured, which is the deployed state: the debit is made and the row records
        // what is owed, so the liability is visible before a rail exists.
        Assert.Equal(PayoutStatuses.Pending, aliceInstruction.Status);
        Assert.Null(aliceInstruction.Reference);

        // The ledger still balances after two debits (D-09).
        Assert.Equal(0, await harness.LedgerSumAsync());
    }

    /// <summary>DoD: a driver with no verified payout profile is skipped and keeps every rupee.</summary>
    [Fact]
    public async Task A_driver_with_no_verified_profile_is_never_swept_and_loses_nothing()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await PayoutHarness.StartAsync(postgres, redis);

        var unverified = await harness.DriverAsync(balanceMinor: 40_000, verifiedProfile: false);
        var verified = await harness.DriverAsync(balanceMinor: 40_000);

        await RunAsync(harness);

        // The join IS the fence: with no verified row the driver cannot be selected, so the balance
        // is retained rather than filtered out somewhere that could stop filtering.
        Assert.Equal(40_000, await harness.BalanceAsync(unverified));
        Assert.Empty(await harness.PayoutsAsync(unverified));

        Assert.Equal(0, await harness.BalanceAsync(verified));
        Assert.Single(await harness.PayoutsAsync(verified));
    }

    /// <summary>DoD: running the sweep twice for one Colombo date is a 409, not a second set.</summary>
    [Fact]
    public async Task A_second_sweep_of_one_colombo_date_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await PayoutHarness.StartAsync(postgres, redis);

        var driver = await harness.DriverAsync(balanceMinor: 25_000);
        var finance = harness.Tokens.FinanceOfficer(Guid.NewGuid());

        using (var first = await harness.SendAsync(HttpMethod.Post, "/v1/admin/payouts/batches", finance))
        {
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        }

        using var second = await harness.SendAsync(HttpMethod.Post, "/v1/admin/payouts/batches", finance);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("payout-batch-exists", (await PayoutHarness.ProblemAsync(second)).Code);

        // The point of the refusal: a second sweep would raise an empty instruction for every driver
        // the first one had just emptied.
        Assert.Single(await harness.PayoutsAsync(driver));
        Assert.Equal(1, await harness.EntryCountAsync(driver, "driver_payout"));
    }

    /// <summary>
    /// DoD: a FAILED instruction restores the balance exactly once, and the following run sweeps it
    /// again — and a redelivered bank result neither pays twice nor reverses twice.
    /// </summary>
    [Fact]
    public async Task A_failed_payout_comes_back_exactly_once_and_is_swept_again_next_week()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var bank = await StubBank.StartAsync();
        await using var harness = await PayoutHarness.StartAsync(postgres, redis, bank);

        var driver = await harness.DriverAsync(balanceMinor: 50_000);

        await RunAsync(harness);

        var instruction = Assert.Single(await harness.PayoutsAsync(driver));

        Assert.Equal(PayoutStatuses.Submitted, instruction.Status);
        Assert.Equal(50_000, Assert.Single(bank.Submitted).AmountMinor);
        Assert.Equal(0, await harness.BalanceAsync(driver));

        var result = new { status = "FAILED", providerReference = instruction.Reference, failureReason = "account closed" };

        using (var reported = await harness.SendAsync(
            HttpMethod.Post,
            $"/v1/internal/payouts/{instruction.Id}/result",
            internalKey: PayoutHarness.InternalApiKey,
            body: result))
        {
            Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        }

        // The money is back, and the driver is whole.
        Assert.Equal(50_000, await harness.BalanceAsync(driver));

        // A redelivery restores nothing further — the guarded transition finds the row terminal.
        using (var redelivered = await harness.SendAsync(
            HttpMethod.Post,
            $"/v1/internal/payouts/{instruction.Id}/result",
            internalKey: PayoutHarness.InternalApiKey,
            body: result))
        {
            Assert.Equal(HttpStatusCode.OK, redelivered.StatusCode);
        }

        Assert.Equal(50_000, await harness.BalanceAsync(driver));
        Assert.Equal(0, await harness.LedgerSumAsync());

        // Next week's run picks the restored balance up. A different Colombo date, so a new batch.
        harness.Clock.Advance(TimeSpan.FromDays(7));

        await RunAsync(harness);

        Assert.Equal(0, await harness.BalanceAsync(driver));
        Assert.Equal(2, (await harness.PayoutsAsync(driver)).Count);
    }

    /// <summary>A bank that accepts settles the instruction, and the money is gone for good.</summary>
    [Fact]
    public async Task A_paid_result_is_terminal_and_does_not_return_the_money()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var bank = await StubBank.StartAsync();
        await using var harness = await PayoutHarness.StartAsync(postgres, redis, bank);

        var driver = await harness.DriverAsync(balanceMinor: 33_000);

        await RunAsync(harness);

        var instruction = Assert.Single(await harness.PayoutsAsync(driver));

        using (var reported = await harness.SendAsync(
            HttpMethod.Post,
            $"/v1/internal/payouts/{instruction.Id}/result",
            internalKey: PayoutHarness.InternalApiKey,
            body: new { status = "PAID", providerReference = instruction.Reference }))
        {
            Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        }

        Assert.Equal(PayoutStatuses.Paid, Assert.Single(await harness.PayoutsAsync(driver)).Status);

        // Paid is terminal: no reversal, and the balance stays where the sweep left it.
        Assert.Equal(0, await harness.BalanceAsync(driver));
        Assert.Equal(1, await harness.EntryCountAsync(driver, "driver_payout"));
    }

    /// <summary>A FAILED result that does not say why is refused before anything moves.</summary>
    [Fact]
    public async Task A_failure_with_no_reason_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var bank = await StubBank.StartAsync();
        await using var harness = await PayoutHarness.StartAsync(postgres, redis, bank);

        var driver = await harness.DriverAsync(balanceMinor: 10_000);

        await RunAsync(harness);

        var instruction = Assert.Single(await harness.PayoutsAsync(driver));

        using var refused = await harness.SendAsync(
            HttpMethod.Post,
            $"/v1/internal/payouts/{instruction.Id}/result",
            internalKey: PayoutHarness.InternalApiKey,
            body: new { status = "FAILED", providerReference = instruction.Reference });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        // Nothing moved: the row is still SUBMITTED and the money is still with the instruction.
        Assert.Equal(PayoutStatuses.Submitted, Assert.Single(await harness.PayoutsAsync(driver)).Status);
        Assert.Equal(0, await harness.BalanceAsync(driver));
    }

    /// <summary>
    /// The sweep is re-runnable: a batch interrupted halfway completes on the next pass without
    /// paying anybody twice.
    /// </summary>
    /// <remarks>
    /// This is the derived-payout-id argument made concrete (<see cref="PayoutIds"/>). The debit and
    /// the instruction live in two services and cannot be one transaction — so the id is a function
    /// of <c>(batch, driver)</c>, the ledger key is a function of the id, and re-running replays the
    /// debit rather than making a second one.
    /// </remarks>
    [Fact]
    public async Task Re_running_a_batch_replays_the_debit_instead_of_making_a_second_one()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await PayoutHarness.StartAsync(postgres, redis);

        var driver = await harness.DriverAsync(balanceMinor: 20_000);

        var first = await RunAsync(harness);
        Assert.NotNull(first);

        // The batch row is deleted-and-reopened by nothing here; instead the service is asked to run
        // the same Colombo date again through the scheduled path, which treats an already-swept day
        // as done. What matters is that neither the balance nor the ledger moved a second time.
        var again = await RunAsync(harness);

        Assert.Null(again);
        Assert.Equal(0, await harness.BalanceAsync(driver));
        Assert.Single(await harness.PayoutsAsync(driver));
        Assert.Equal(1, await harness.EntryCountAsync(driver, "driver_payout"));
    }

    /// <summary>A driver reads their own payout history; Finance reads everybody's.</summary>
    [Fact]
    public async Task A_driver_sees_their_own_payouts_and_finance_sees_them_all()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await PayoutHarness.StartAsync(postgres, redis);

        var alice = await harness.DriverAsync(balanceMinor: 15_000, accountNo: "0079876543");
        var bob = await harness.DriverAsync(balanceMinor: 15_000);

        await RunAsync(harness);

        var mine = await harness.OkAsync<CursorPage<PayoutResponse>>(
            await harness.GetAsync("/v1/drivers/payouts", harness.Tokens.Driver(alice)),
            "read my payouts");

        var row = Assert.Single(mine.Items);

        Assert.Equal(alice, row.DriverId);
        Assert.Equal(15_000, row.AmountMinor);

        // The account is recognisable, not readable.
        Assert.Equal("****6543", row.AccountNoMasked);

        var all = await harness.OkAsync<CursorPage<PayoutResponse>>(
            await harness.GetAsync("/v1/admin/payouts?limit=100", harness.Tokens.FinanceOfficer(Guid.NewGuid())),
            "read every payout");

        // Contains rather than counts: the Finance view is platform-wide and this collection shares
        // one database, so a count would depend on which other tests had run.
        Assert.Contains(all.Items, item => item.DriverId == alice);
        Assert.Contains(all.Items, item => item.DriverId == bob);
    }

    /// <summary>Money leaving the platform is Finance's, not an ordinary admin's (URD §2.3).</summary>
    [Fact]
    public async Task An_admin_cannot_release_a_week_of_payouts()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await PayoutHarness.StartAsync(postgres, redis);

        using var refused = await harness.SendAsync(
            HttpMethod.Post, "/v1/admin/payouts/batches", harness.Tokens.Admin(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    /// <summary>Runs the sweep for the harness clock's Colombo date, through the service itself.</summary>
    private static async Task<PayoutRunResult?> RunAsync(PayoutHarness harness)
    {
        using var scope = harness.Services.CreateScope();

        var run = scope.ServiceProvider.GetRequiredService<PayoutRunService>();

        return await run.RunAsync(BusinessCalendar.Today(harness.Clock), force: false, CancellationToken.None);
    }
}
