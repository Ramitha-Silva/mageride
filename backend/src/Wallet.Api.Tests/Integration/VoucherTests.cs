using System.Net;
using System.Net.Http.Json;
using MageRide.Wallet.Endpoints;
using MageRide.Wallet.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Wallet.Tests.Integration;

/// <summary>
/// This component's second definition of done: <b>a Rs 1,000 voucher at a 10 % tier debits Rs 900 and
/// credits Rs 1,000 exactly once, idempotent on the gateway reference</b> (US-9.19, AL-01).
/// </summary>
[Collection<WalletCollection>]
public sealed class VoucherTests(PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
{
    /// <summary>D5' §9.3's worked example, against migration 1901's seeded ladder.</summary>
    [Fact]
    public async Task The_worked_example_charges_900_and_credits_1000()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync();

        using var response = await harness.PostAsync(
            "/v1/wallet/voucher/purchase",
            new { denominationMinor = 100_000, gatewayRef = "onepay-voucher-1" },
            driver.Bearer);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var purchase = (await response.Content.ReadFromJsonAsync<VoucherPurchaseResponse>())!;

        // Rs 1,000 = 100,000 minor units, 1000 bps = 10 %, so the price is 90,000 and the credit 100,000.
        Assert.Equal(100_000, purchase.DenominationMinor);
        Assert.Equal(1_000, purchase.DiscountBps);
        Assert.Equal(90_000, purchase.PaidMinor);
        Assert.Equal(100_000, purchase.CreditedMinor);
        Assert.Equal(100_000, purchase.BalanceAfterMinor);

        // The wallet has the face value, not the price.
        Assert.Equal(100_000, await harness.BalanceAsync(driver.Id));

        // Two legs, equal and opposite: the face value moves and the discount is not a posting.
        var postings = await harness.PostingsAsync(purchase.EntryId!.Value);

        Assert.Equal(2, postings.Count);
        Assert.Equal(0, postings.Sum(posting => posting.AmountMinor));
        Assert.Contains(postings, posting => posting.AmountMinor == 100_000);
        Assert.Contains(postings, posting => posting.AmountMinor == -100_000);
    }

    /// <summary>Every seeded rung, so a rate change cannot silently break one tier's arithmetic.</summary>
    [Theory]
    [InlineData(100_000, 1_000, 90_000)]
    [InlineData(200_000, 1_100, 178_000)]
    [InlineData(300_000, 1_200, 264_000)]
    [InlineData(500_000, 1_300, 435_000)]
    [InlineData(1_000_000, 1_500, 850_000)]
    public async Task Every_seeded_tier_prices_correctly(long denomination, int bps, long paid)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync();

        using var response = await harness.PostAsync(
            "/v1/wallet/voucher/purchase", new { denominationMinor = denomination }, driver.Bearer);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var purchase = (await response.Content.ReadFromJsonAsync<VoucherPurchaseResponse>())!;

        Assert.Equal(bps, purchase.DiscountBps);
        Assert.Equal(paid, purchase.PaidMinor);
        Assert.Equal(denomination, purchase.CreditedMinor);
    }

    /// <summary>
    /// The other half of the DoD: the same gateway reference credits once, however many times it arrives.
    /// </summary>
    [Fact]
    public async Task The_same_gateway_reference_credits_exactly_once()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync();
        var body = new { denominationMinor = 100_000, gatewayRef = "onepay-voucher-replayed" };

        // Different Idempotency-Key headers each time, so the kernel's replay log cannot be what saves
        // this — the gateway reference has to.
        using var first = await harness.PostAsync("/v1/wallet/voucher/purchase", body, driver.Bearer);
        using var second = await harness.PostAsync("/v1/wallet/voucher/purchase", body, driver.Bearer);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var firstPurchase = (await first.Content.ReadFromJsonAsync<VoucherPurchaseResponse>())!;
        var secondPurchase = (await second.Content.ReadFromJsonAsync<VoucherPurchaseResponse>())!;

        Assert.Equal(firstPurchase.PurchaseId, secondPurchase.PurchaseId);
        Assert.Equal(100_000, await harness.BalanceAsync(driver.Id));
        Assert.Equal(1, await harness.EntryCountAsync("voucher_purchase"));
    }

    /// <summary>
    /// A denomination between tiers is refused, not interpolated: the rate is set per voucher value
    /// (AL-01), and inventing one would invent a number somebody is paid.
    /// </summary>
    [Fact]
    public async Task A_denomination_between_tiers_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync();

        using var response = await harness.PostAsync(
            "/v1/wallet/voucher/purchase", new { denominationMinor = 150_000 }, driver.Bearer);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var (code, problem) = await WalletHarness.ProblemAsync(response);

        Assert.Equal("validation-failed", code);
        Assert.Contains(
            "100000",
            problem.GetProperty("errors").GetProperty("denominationMinor")[0].GetString());

        Assert.Equal(0, await harness.BalanceAsync(driver.Id));
    }

    /// <summary>An inactive tier is not a price anybody can pay.</summary>
    [Fact]
    public async Task An_inactive_tier_cannot_be_bought_and_is_not_listed()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var admin = await harness.CreateUserAsync("admin");
        var driver = await harness.CreateDriverAsync();

        try
        {
            using var deactivate = await harness.PutAsync(
                "/v1/wallet/admin/voucher-discount-tiers",
                new { tiers = new[] { new { denominationMinor = 1_000_000, discountBps = 1_500, active = false } } },
                harness.Tokens.Admin(admin));

            Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

            var ladder = await harness.GetAsync<VoucherTiersResponse>(
                "/v1/wallet/voucher/discount-tiers", driver.Bearer);

            Assert.DoesNotContain(ladder.Tiers, tier => tier.DenominationMinor == 1_000_000);

            using var purchase = await harness.PostAsync(
                "/v1/wallet/voucher/purchase", new { denominationMinor = 1_000_000 }, driver.Bearer);

            Assert.Equal(HttpStatusCode.BadRequest, purchase.StatusCode);
        }
        finally
        {
            // 1901's ladder is reference data every other test reads, so this one puts it back.
            using var restore = await harness.PutAsync(
                "/v1/wallet/admin/voucher-discount-tiers",
                new { tiers = new[] { new { denominationMinor = 1_000_000, discountBps = 1_500, active = true } } },
                harness.Tokens.Admin(admin));

            Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        }
    }

    /// <summary>
    /// The admin view carries the usage that makes the aggregate reseller margin visible (US-9A.15), and
    /// only the three roles that may see it.
    /// </summary>
    [Fact]
    public async Task The_admin_view_shows_usage_and_refuses_everybody_else()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync();
        var admin = await harness.CreateUserAsync("admin");
        var finance = await harness.CreateUserAsync("finance_officer");

        using var bought = await harness.PostAsync(
            "/v1/wallet/voucher/purchase", new { denominationMinor = 200_000 }, driver.Bearer);
        Assert.Equal(HttpStatusCode.Created, bought.StatusCode);

        foreach (var bearer in new[] { harness.Tokens.Admin(admin), harness.Tokens.FinanceOfficer(finance) })
        {
            var tiers = await harness.GetAsync<AdminVoucherTiersResponse>(
                "/v1/wallet/admin/voucher-discount-tiers", bearer);

            var rung = Assert.Single(tiers.Tiers, tier => tier.DenominationMinor == 200_000);

            Assert.Equal(1, rung.PurchaseCount);
            Assert.Equal(200_000, rung.PurchasedValueMinor);
        }

        // A driver cannot read the admin view, and a Support CSR — an internal role with no cell in
        // URD §2.3's money row — cannot either.
        using var byDriver = await harness.GetAsync("/v1/wallet/admin/voucher-discount-tiers", driver.Bearer);
        using var byCsr = await harness.GetAsync(
            "/v1/wallet/admin/voucher-discount-tiers", harness.Tokens.SupportCsr(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, byDriver.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byCsr.StatusCode);
    }

    /// <summary>Two rates for one denomination is a rejection, not a coin toss.</summary>
    [Fact]
    public async Task A_duplicated_denomination_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var admin = await harness.CreateUserAsync("admin");

        using var response = await harness.PutAsync(
            "/v1/wallet/admin/voucher-discount-tiers",
            new
            {
                tiers = new[]
                {
                    new { denominationMinor = 400_000, discountBps = 1_000, active = true },
                    new { denominationMinor = 400_000, discountBps = 2_000, active = true },
                },
            },
            harness.Tokens.Admin(admin));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation-failed", (await WalletHarness.ProblemAsync(response)).Code);
    }
}
