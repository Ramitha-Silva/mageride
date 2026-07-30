using System.Net;
using Dapper;
using MageRide.Wallet.Endpoints;
using MageRide.Wallet.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Wallet.Tests.Integration;

/// <summary>
/// This component's first definition of done: <b>every ledger entry balances to zero, and the DB
/// trigger rejects anything else</b> (D-09).
/// </summary>
[Collection<WalletCollection>]
public sealed class LedgerTests(PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
{
    /// <summary>
    /// The invariant, from both sides: each entry sums to zero and so does the whole table, after a
    /// mixture of every operation this service can perform.
    /// </summary>
    [Fact]
    public async Task Every_entry_and_the_whole_ledger_sum_to_zero()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var alice = await harness.CreateDriverAsync(openingBalanceMinor: 500_00);
        var bob = await harness.CreateDriverAsync();

        // A voucher, a transfer and a daily fee — a credit, a two-driver movement and a debit.
        using var voucher = await harness.PostAsync(
            "/v1/wallet/voucher/purchase", new { denominationMinor = 100_000 }, alice.Bearer);
        Assert.Equal(HttpStatusCode.Created, voucher.StatusCode);

        using var transfer = await harness.PostAsync(
            "/v1/wallet/credit-transfer/initiate",
            new { recipientDriverId = bob.Id, amountMinor = 25_000 },
            alice.Bearer);
        Assert.Equal(HttpStatusCode.Created, transfer.StatusCode);

        using var fee = await harness.PostAsync(
            $"/v1/internal/wallet/{alice.Id}/debit",
            new
            {
                amountMinor = 6_000,
                kind = "daily_fee",
                idempotencyKey = $"daily_fee:{alice.Id}:{Guid.NewGuid()}:2026-07-30",
            },
            internalKey: WalletHarness.InternalApiKey);
        Assert.Equal(HttpStatusCode.OK, fee.StatusCode);

        // Every entry, one at a time.
        await using var connection = await harness.OpenAsync();

        var unbalanced = await connection.QueryAsync<Guid>(
            """
            SELECT entry_id FROM billing.journal_postings
             GROUP BY entry_id HAVING sum(amount_minor) <> 0;
            """);

        Assert.Empty(unbalanced);

        // And the table as a whole — which is the stronger statement: money was created or destroyed
        // nowhere, not merely balanced entry by entry.
        Assert.Equal(0, await harness.LedgerSumAsync());

        // The read model agrees with the master, because they are written in one transaction.
        Assert.Equal(await harness.BalanceAsync(alice.Id), await harness.MirrorBalanceAsync(alice.Id));
        Assert.Equal(await harness.BalanceAsync(bob.Id), await harness.MirrorBalanceAsync(bob.Id));
    }

    /// <summary>
    /// The trigger, directly: a single-leg entry cannot be committed even by a caller that bypasses
    /// this service entirely.
    /// </summary>
    /// <remarks>
    /// The check is <c>DEFERRABLE INITIALLY DEFERRED</c>, so the INSERT succeeds and the **COMMIT**
    /// fails. That is what lets a two-leg entry be written one row at a time, and it is why this test
    /// wraps an explicit transaction rather than asserting on the insert.
    /// </remarks>
    [Fact]
    public async Task The_database_rejects_a_single_leg_entry_at_commit()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync(openingBalanceMinor: 10_000);

        await using var connection = await harness.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var entryId = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO billing.journal_entries (kind, idempotency_key, description)
            VALUES ('adjustment', @Key, 'deliberately unbalanced')
            RETURNING id;
            """,
            new { Key = $"unbalanced:{Guid.NewGuid()}" },
            transaction);

        var accountId = await connection.ExecuteScalarAsync<Guid>(
            "SELECT id FROM billing.accounts WHERE owner_type='driver' AND owner_id = @DriverId;",
            new { DriverId = driver.Id },
            transaction);

        // One leg. Accepted by the INSERT — the trigger is deferred.
        await connection.ExecuteAsync(
            """
            INSERT INTO billing.journal_postings (entry_id, account_id, amount_minor)
            VALUES (@EntryId, @AccountId, 1000);
            """,
            new { EntryId = entryId, AccountId = accountId },
            transaction);

        var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(
            async () => await transaction.CommitAsync());

        Assert.Contains("not balanced", exception.MessageText, StringComparison.OrdinalIgnoreCase);

        // And nothing survived: the whole transaction went with the failed commit.
        Assert.Equal(0, await harness.EntrySumAsync(entryId));
        Assert.Equal(0, await harness.LedgerSumAsync());
    }

    /// <summary>
    /// A driver's wallet cannot go negative — §10 leaves that to the application, so the application
    /// answers <c>402</c>.
    /// </summary>
    [Fact]
    public async Task A_debit_beyond_the_balance_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync(openingBalanceMinor: 5_000);

        using var response = await harness.PostAsync(
            $"/v1/internal/wallet/{driver.Id}/debit",
            new
            {
                amountMinor = 5_001,
                kind = "daily_fee",
                idempotencyKey = $"daily_fee:{driver.Id}:overdraw",
            },
            internalKey: WalletHarness.InternalApiKey);

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        Assert.Equal("insufficient-wallet", (await WalletHarness.ProblemAsync(response)).Code);

        // Nothing moved, and — the part that matters — the *entry* did not survive either. A rolled-back
        // posting that left its journal row behind would burn the idempotency key, so the retry after a
        // top-up would replay "nothing happened" for ever.
        Assert.Equal(5_000, await harness.BalanceAsync(driver.Id));

        await using var connection = await harness.OpenAsync();

        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "SELECT count(*)::int FROM billing.journal_entries WHERE idempotency_key = @Key;",
                new { Key = $"daily_fee:{driver.Id}:overdraw" }));
    }

    /// <summary>
    /// The platform account is exempt, and it must be: the other side of every credit is negative by
    /// construction, which is what double entry means.
    /// </summary>
    [Fact]
    public async Task The_platform_account_goes_negative_and_that_is_correct()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync(openingBalanceMinor: 100_000);

        await using var connection = await harness.OpenAsync();

        var platform = await connection.ExecuteScalarAsync<long>(
            "SELECT balance_minor FROM billing.accounts WHERE owner_type='platform';");

        Assert.Equal(-100_000, platform);
        Assert.Equal(100_000, await harness.BalanceAsync(driver.Id));
        Assert.Equal(0, await harness.LedgerSumAsync());
    }

    /// <summary>
    /// The same ledger key posts once. This is the guarantee subscription-svc's daily fee depends on
    /// (D-13's "charging twice on the same (driver, vehicle, date) debits once").
    /// </summary>
    [Fact]
    public async Task A_repeated_ledger_key_posts_once_and_reports_the_replay()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync(openingBalanceMinor: 50_000);
        var key = $"daily_fee:{driver.Id}:{Guid.NewGuid()}:2026-07-30";

        var body = new { amountMinor = 6_000, kind = "daily_fee", idempotencyKey = key };

        using var first = await harness.PostAsync(
            $"/v1/internal/wallet/{driver.Id}/debit", body, internalKey: WalletHarness.InternalApiKey);
        using var second = await harness.PostAsync(
            $"/v1/internal/wallet/{driver.Id}/debit", body, internalKey: WalletHarness.InternalApiKey);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var firstResult = (await first.Content.ReadFromJsonAsync<LedgerPostingResultResponse>())!;
        var secondResult = (await second.Content.ReadFromJsonAsync<LedgerPostingResultResponse>())!;

        Assert.False(firstResult.Replayed);
        Assert.True(secondResult.Replayed);
        Assert.Equal(firstResult.EntryId, secondResult.EntryId);

        // Debited once.
        Assert.Equal(44_000, await harness.BalanceAsync(driver.Id));

        // And exactly one history line, because `ux_wallet_tx_account_entry` is the projection's guard.
        await using var connection = await harness.OpenAsync();

        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                """
                SELECT count(*)::int FROM billing.wallet_transactions t
                  JOIN billing.journal_entries e ON e.id = t.entry_id
                 WHERE e.idempotency_key = @Key;
                """,
                new { Key = key }));
    }

    /// <summary>
    /// The internal plane only accepts the kinds a spec names for it — and never the three this service
    /// owns endpoints for.
    /// </summary>
    [Theory]
    [InlineData("debit", "topup")]
    [InlineData("debit", "tip_payout")]
    [InlineData("credit", "daily_fee")]
    [InlineData("credit", "voucher_purchase")]
    [InlineData("credit", "driver_transfer")]
    [InlineData("debit", "reseller_commission")]
    public async Task An_unwhitelisted_kind_is_refused(string route, string kind)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync(openingBalanceMinor: 100_000);

        using var response = await harness.PostAsync(
            $"/v1/internal/wallet/{driver.Id}/{route}",
            new { amountMinor = 1_000, kind, idempotencyKey = $"test:{Guid.NewGuid()}" },
            internalKey: WalletHarness.InternalApiKey);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var (code, problem) = await WalletHarness.ProblemAsync(response);

        Assert.Equal("validation-failed", code);
        Assert.True(problem.GetProperty("errors").TryGetProperty("kind", out _));
    }

    /// <summary>The internal plane is internal: no key, no route.</summary>
    [Fact]
    public async Task The_ledger_seam_is_refused_without_the_internal_key()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync();
        var body = new { amountMinor = 1_000, kind = "adjustment", idempotencyKey = $"test:{Guid.NewGuid()}" };

        using var anonymous = await harness.PostAsync($"/v1/internal/wallet/{driver.Id}/credit", body);
        using var wrongKey = await harness.PostAsync(
            $"/v1/internal/wallet/{driver.Id}/credit", body, internalKey: "not-the-key");

        // Even a valid admin bearer is not a substitute: this is a service-to-service surface.
        using var bearer = await harness.PostAsync(
            $"/v1/internal/wallet/{driver.Id}/credit", body, harness.Tokens.Admin(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongKey.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, bearer.StatusCode);
    }

    /// <summary>
    /// A movement writes the D-08 cache dispatch-svc's gate reads, and the outbox row that tells every
    /// other replica.
    /// </summary>
    [Fact]
    public async Task A_movement_writes_the_dispatch_cache_and_an_outbox_event()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync(openingBalanceMinor: 30_000);

        // The seeded credit already went through the ledger, so the cache is warm at the new balance.
        Assert.Equal(30_000, await harness.CachedBalanceAsync(driver.Id));

        using var debit = await harness.PostAsync(
            $"/v1/internal/wallet/{driver.Id}/debit",
            new { amountMinor = 6_000, kind = "daily_fee", idempotencyKey = $"daily_fee:{driver.Id}:cache" },
            internalKey: WalletHarness.InternalApiKey);

        Assert.Equal(HttpStatusCode.OK, debit.StatusCode);

        // Write-through, not delete: D5' §9.2's "debit-invalidated" is satisfied more strongly by
        // replacing the value, and C034's own test expects wallet-svc to write it.
        Assert.Equal(24_000, await harness.CachedBalanceAsync(driver.Id));

        var events = await harness.OutboxAsync();

        Assert.Contains(events, row => row.EventType == "wallet.credited" && row.AggregateId == driver.Id);
        Assert.Contains(events, row => row.EventType == "wallet.debited" && row.AggregateId == driver.Id);

        // The payload carries the signed amount and the balance after, so a consumer needs no second
        // read to refresh its cache.
        var debited = events.Last(row => row.EventType == "wallet.debited");

        Assert.Equal(-6_000, debited.Number("amountMinor"));
        Assert.Equal(24_000, debited.Number("balanceAfterMinor"));
        Assert.Equal("daily_fee", debited.Text("kind"));
    }
}
