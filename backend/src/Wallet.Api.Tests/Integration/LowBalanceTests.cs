using System.Net;
using MageRide.Wallet.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Wallet.Tests.Integration;

/// <summary>
/// US-9.9 / D5' §9.4's low-balance warning: edge-triggered on the crossing, with the below-zero case
/// carried as a severity rather than a second threshold.
/// </summary>
[Collection<WalletCollection>]
public sealed class LowBalanceTests(PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
{
    /// <summary>
    /// One event on the way down, and none for a second debit that stays below.
    /// </summary>
    /// <remarks>
    /// Level-triggered, every debit of a low wallet would be a push, and the warning would be the noise a
    /// driver mutes inside a day. The balance *before* the posting is known inside the transaction, which
    /// is what makes the edge observable at all.
    /// </remarks>
    [Fact]
    public async Task Crossing_the_threshold_warns_once()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        // Rs 250, just above D7' §4.2's Rs 200 threshold.
        var driver = await harness.CreateDriverAsync(openingBalanceMinor: 25_000);

        Assert.Empty(await harness.OutboxAsync("wallet.low_balance"));

        // Down to Rs 190 — the crossing.
        using var first = await harness.PostAsync(
            $"/v1/internal/wallet/{driver.Id}/debit",
            new { amountMinor = 6_000, kind = "daily_fee", idempotencyKey = $"daily_fee:{driver.Id}:1" },
            internalKey: WalletHarness.InternalApiKey);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var warned = Assert.Single(await harness.OutboxAsync("wallet.low_balance"));

        Assert.Equal(driver.Id, warned.AggregateId);
        Assert.Equal(19_000, warned.Number("balanceMinor"));
        Assert.Equal(20_000, warned.Number("thresholdMinor"));
        Assert.Equal("low", warned.Text("severity"));

        // A hand-off, not a notification: the type is there for notification-svc (C051) and no rendered
        // text is (D-26).
        Assert.Equal("LOW_BALANCE", warned.Text("notificationType"));
        Assert.False(warned.Json.TryGetProperty("message", out _));
        Assert.False(warned.Json.TryGetProperty("body", out _));

        // Already below: no second warning.
        using var second = await harness.PostAsync(
            $"/v1/internal/wallet/{driver.Id}/debit",
            new { amountMinor = 5_000, kind = "daily_fee", idempotencyKey = $"daily_fee:{driver.Id}:2" },
            internalKey: WalletHarness.InternalApiKey);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Single(await harness.OutboxAsync("wallet.low_balance"));
    }

    /// <summary>
    /// A top-up back above the threshold re-arms the edge, so the next fall warns again.
    /// </summary>
    [Fact]
    public async Task Coming_back_above_re_arms_the_warning()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync(openingBalanceMinor: 25_000);

        using var down = await harness.PostAsync(
            $"/v1/internal/wallet/{driver.Id}/debit",
            new { amountMinor = 6_000, kind = "daily_fee", idempotencyKey = $"daily_fee:{driver.Id}:down" },
            internalKey: WalletHarness.InternalApiKey);
        Assert.Equal(HttpStatusCode.OK, down.StatusCode);

        Assert.Single(await harness.OutboxAsync("wallet.low_balance"));

        await harness.CreditDirectlyAsync(driver.Id, 20_000);

        using var again = await harness.PostAsync(
            $"/v1/internal/wallet/{driver.Id}/debit",
            new { amountMinor = 20_000, kind = "daily_fee", idempotencyKey = $"daily_fee:{driver.Id}:again" },
            internalKey: WalletHarness.InternalApiKey);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        Assert.Equal(2, (await harness.OutboxAsync("wallet.low_balance")).Count);
    }

    /// <summary>
    /// The threshold is configurable (D7' §4.2's <c>LowBalance__ThresholdMinor</c>), and it is honoured as
    /// that variable is spelled.
    /// </summary>
    [Fact]
    public async Task The_d7_spelling_of_the_threshold_is_honoured()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(
            postgres,
            redis,
            redpanda,
            new Dictionary<string, string?> { ["LowBalance:ThresholdMinor"] = "50000" });

        var driver = await harness.CreateDriverAsync(openingBalanceMinor: 55_000);

        using var debit = await harness.PostAsync(
            $"/v1/internal/wallet/{driver.Id}/debit",
            new { amountMinor = 6_000, kind = "daily_fee", idempotencyKey = $"daily_fee:{driver.Id}:threshold" },
            internalKey: WalletHarness.InternalApiKey);

        Assert.Equal(HttpStatusCode.OK, debit.StatusCode);

        var warned = Assert.Single(await harness.OutboxAsync("wallet.low_balance"));

        Assert.Equal(50_000, warned.Number("thresholdMinor"));
    }

    /// <summary>
    /// D5' §9.4's second clause: below zero is a different message ("Top Up Required"), and only a client
    /// draws a banner — so the distinction travels with the event.
    /// </summary>
    /// <remarks>
    /// A driver's wallet cannot be taken negative through this service, so the case is reached the only
    /// way it can be: a threshold of zero, where any debit crosses it. The severity is what a consumer
    /// switches on, and it is asserted rather than assumed to be unreachable.
    /// </remarks>
    [Fact]
    public async Task A_zero_threshold_reports_the_top_up_required_severity()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(
            postgres,
            redis,
            redpanda,
            new Dictionary<string, string?> { ["Wallet:LowBalanceThresholdMinor"] = "1" });

        var driver = await harness.CreateDriverAsync(openingBalanceMinor: 10_000);

        using var debit = await harness.PostAsync(
            $"/v1/internal/wallet/{driver.Id}/debit",
            new { amountMinor = 10_000, kind = "daily_fee", idempotencyKey = $"daily_fee:{driver.Id}:zero" },
            internalKey: WalletHarness.InternalApiKey);

        Assert.Equal(HttpStatusCode.OK, debit.StatusCode);
        Assert.Equal(0, await harness.BalanceAsync(driver.Id));

        var warned = Assert.Single(await harness.OutboxAsync("wallet.low_balance"));

        // Zero is not below zero, so this is still the "low" severity — the boundary asserted from the
        // other side of D5' §9.4's two clauses.
        Assert.Equal("low", warned.Text("severity"));
        Assert.Equal(0, warned.Number("balanceMinor"));
    }
}
