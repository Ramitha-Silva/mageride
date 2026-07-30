using System.Net;
using System.Net.Http.Headers;
using MageRide.Shared.Primitives;
using MageRide.Wallet.Endpoints;
using MageRide.Wallet.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Wallet.Tests.Integration;

/// <summary>
/// <c>GET /v1/wallet/{userId}</c> and its history (US-9.7, US-9A.19) — including who may read whose.
/// </summary>
[Collection<WalletCollection>]
public sealed class WalletReadTests(PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
{
    [Fact]
    public async Task A_driver_reads_their_own_balance_and_history()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync(openingBalanceMinor: 120_000);

        using var fee = await harness.PostAsync(
            $"/v1/internal/wallet/{driver.Id}/debit",
            new { amountMinor = 6_000, kind = "daily_fee", idempotencyKey = $"daily_fee:{driver.Id}:read" },
            internalKey: WalletHarness.InternalApiKey);
        Assert.Equal(HttpStatusCode.OK, fee.StatusCode);

        var wallet = await harness.GetAsync<WalletResponse>($"/v1/wallet/{driver.Id}", driver.Bearer);

        Assert.Equal(driver.Id, wallet.UserId);
        Assert.Equal(114_000, wallet.BalanceMinor);
        Assert.Equal("LKR", wallet.Currency);

        // A driver carries no accrued debt: §11.12 answers a driver cancellation with reputation rather
        // than money, so `available` and `balance` agree.
        Assert.Equal(0, wallet.OutstandingDebtMinor);
        Assert.Equal(wallet.BalanceMinor, wallet.AvailableMinor);

        var history = await harness.GetAsync<CursorPage<WalletTransactionResponse>>(
            $"/v1/wallet/{driver.Id}/transactions", driver.Bearer);

        Assert.Equal(2, history.Items.Count);

        // Newest first, and signed: the debit is negative, which is one of the ledger columns §0 exempts
        // from the non-negative rule.
        Assert.Equal("daily_fee", history.Items[0].Kind);
        Assert.Equal(-6_000, history.Items[0].AmountMinor);
        Assert.Equal(114_000, history.Items[0].BalanceAfterMinor);
        Assert.Equal("adjustment", history.Items[1].Kind);
        Assert.Equal(120_000, history.Items[1].AmountMinor);
    }

    /// <summary>A wallet that has never moved is zero, not a 404.</summary>
    [Fact]
    public async Task An_untouched_wallet_reads_as_zero()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync();

        var wallet = await harness.GetAsync<WalletResponse>($"/v1/wallet/{driver.Id}", driver.Bearer);

        Assert.Equal(0, wallet.BalanceMinor);

        var history = await harness.GetAsync<CursorPage<WalletTransactionResponse>>(
            $"/v1/wallet/{driver.Id}/transactions", driver.Bearer);

        Assert.Empty(history.Items);
        Assert.False(history.HasMore);
    }

    /// <summary>
    /// Somebody else's wallet is a 403; a back-office role may read it (US-24.9/24.10's read-only tabs).
    /// </summary>
    [Fact]
    public async Task Another_wallet_is_forbidden_unless_the_caller_is_back_office()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var mine = await harness.CreateDriverAsync(openingBalanceMinor: 10_000);
        var nosy = await harness.CreateDriverAsync();

        using var byDriver = await harness.GetAsync($"/v1/wallet/{mine.Id}", nosy.Bearer);

        Assert.Equal(HttpStatusCode.Forbidden, byDriver.StatusCode);

        var byAdmin = await harness.GetAsync<WalletResponse>(
            $"/v1/wallet/{mine.Id}", harness.Tokens.Admin(Guid.NewGuid()));

        Assert.Equal(10_000, byAdmin.BalanceMinor);

        var byCsr = await harness.GetAsync<WalletResponse>(
            $"/v1/wallet/{mine.Id}", harness.Tokens.SupportCsr(Guid.NewGuid()));

        Assert.Equal(10_000, byCsr.BalanceMinor);
    }

    [Fact]
    public async Task An_anonymous_read_is_unauthorized()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync();

        using var response = await harness.GetAsync($"/v1/wallet/{driver.Id}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>The statement download (US-9A.19). CSV is implemented; PDF says so rather than faking it.</summary>
    [Fact]
    public async Task A_csv_statement_is_served_and_a_pdf_is_declined_with_a_reason()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync(openingBalanceMinor: 75_000);

        using var csvRequest = new HttpRequestMessage(
            HttpMethod.Get, $"/v1/wallet/{driver.Id}/transactions");
        csvRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", driver.Bearer);
        csvRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/csv"));

        using var csv = await harness.Client.SendAsync(csvRequest);

        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);

        var text = await csv.Content.ReadAsStringAsync();

        Assert.StartsWith("occurredAt,kind,amountMinor,balanceAfterMinor,currency,reference", text);
        Assert.Contains("adjustment,75000,75000,LKR", text);

        using var pdfRequest = new HttpRequestMessage(
            HttpMethod.Get, $"/v1/wallet/{driver.Id}/transactions");
        pdfRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", driver.Bearer);
        pdfRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/pdf"));

        using var pdf = await harness.Client.SendAsync(pdfRequest);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, pdf.StatusCode);
        Assert.Contains(
            "renderer",
            (await WalletHarness.ProblemAsync(pdf)).Body.GetProperty("detail").GetString());
    }

    /// <summary>
    /// The history pages by keyset, so a page boundary that falls inside a burst of same-instant lines
    /// neither repeats nor skips one.
    /// </summary>
    [Fact]
    public async Task The_history_pages_without_repeating_a_line()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync();

        // Seven credits on a frozen clock: every line shares one timestamp, which is exactly the case a
        // timestamp-only cursor gets wrong.
        for (var i = 0; i < 7; i++)
        {
            await harness.CreditDirectlyAsync(driver.Id, 1_000);
        }

        var seen = new List<Guid>();
        string? cursor = null;

        do
        {
            var page = await harness.GetAsync<CursorPage<WalletTransactionResponse>>(
                $"/v1/wallet/{driver.Id}/transactions?limit=3{(cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}")}",
                driver.Bearer);

            seen.AddRange(page.Items.Select(item => item.TransactionId));
            cursor = page.Cursor;
        }
        while (cursor is not null);

        Assert.Equal(7, seen.Count);
        Assert.Equal(7, seen.Distinct().Count());
        Assert.Equal(7_000, await harness.BalanceAsync(driver.Id));
    }
}
