using System.Net;
using System.Text.Json;
using MageRide.Subscriptions.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Subscriptions.Tests.Integration;

/// <summary>
/// D3' subscription-svc's credit-transfer and bulk-voucher spellings, which forward to wallet-svc.
/// </summary>
/// <remarks>
/// What is asserted is that the D3'-spelled path reaches the same money as the <c>/v1/wallet/**</c>
/// spelling and carries the caller's own identity — not that the transfer arithmetic is right, which is
/// C046's suite and C046's code.
/// </remarks>
[Collection<SubscriptionCollection>]
public sealed class CreditForwardingTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>US-9.20/9.21: exact value, no commission, by Driver ID.</summary>
    [Fact]
    public async Task A_direct_transfer_through_the_d3_path_moves_the_exact_value()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var sender = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var recipient = await harness.Seed.DriverAsync();

        using var response = await harness.PostAsync(
            "/v1/transfers/driver",
            new { recipientDriverId = recipient.Id.ToString(), amountMinor = 25_000 },
            sender.Bearer);

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"POST /v1/transfers/driver returned {(int)response.StatusCode}: "
            + await response.Content.ReadAsStringAsync());

        Assert.Equal(75_000, await harness.BalanceAsync(sender.Id));
        Assert.Equal(25_000, await harness.BalanceAsync(recipient.Id));

        // AL-01: the exact value moved and no fee leg exists to be written. One entry, and the ledger
        // still sums to zero.
        Assert.Equal(1, await harness.EntryCountAsync("driver_transfer"));
        Assert.Equal(0, await harness.LedgerSumAsync());
    }

    /// <summary>The request / approve pair, both through the D3' spelling.</summary>
    [Fact]
    public async Task A_requested_transfer_can_be_raised_and_approved_through_the_d3_paths()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var holder = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var requester = await harness.Seed.DriverAsync();

        Guid transferId;

        using (var raised = await harness.PostAsync(
                   "/v1/subscriptions/credit-transfer/request",
                   new { holderDriverId = holder.Id.ToString(), amountMinor = 30_000 },
                   requester.Bearer))
        {
            Assert.Equal(HttpStatusCode.Created, raised.StatusCode);

            using var document = JsonDocument.Parse(await raised.Content.ReadAsStringAsync());
            transferId = document.RootElement.GetProperty("transferId").GetGuid();
        }

        // The holder's inbox, through the D3' spelling.
        using (var pending = await harness.GetAsync(
                   "/v1/subscriptions/credit-transfer/pending", holder.Bearer))
        {
            Assert.Equal(HttpStatusCode.OK, pending.StatusCode);

            using var document = JsonDocument.Parse(await pending.Content.ReadAsStringAsync());
            Assert.Contains(
                document.RootElement.GetProperty("items").EnumerateArray(),
                item => item.GetProperty("transferId").GetGuid() == transferId);
        }

        using (var approved = await harness.PostAsync(
                   $"/v1/subscriptions/credit-transfer/{transferId}/approve", null, holder.Bearer))
        {
            Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        }

        Assert.Equal(70_000, await harness.BalanceAsync(holder.Id));
        Assert.Equal(30_000, await harness.BalanceAsync(requester.Id));
    }

    /// <summary>
    /// The caller's own bearer travels: wallet-svc's "not yours is a 404" rule is intact through the
    /// proxy, so this hop grants nothing the driver did not already have.
    /// </summary>
    [Fact]
    public async Task A_transfer_that_is_not_the_callers_stays_a_404_through_the_proxy()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var holder = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var requester = await harness.Seed.DriverAsync();
        var stranger = await harness.Seed.DriverAsync();

        Guid transferId;

        using (var raised = await harness.PostAsync(
                   "/v1/subscriptions/credit-transfer/request",
                   new { holderDriverId = holder.Id.ToString(), amountMinor = 10_000 },
                   requester.Bearer))
        {
            using var document = JsonDocument.Parse(await raised.Content.ReadAsStringAsync());
            transferId = document.RootElement.GetProperty("transferId").GetGuid();
        }

        using var byStranger = await harness.PostAsync(
            $"/v1/subscriptions/credit-transfer/{transferId}/approve", null, stranger.Bearer);

        Assert.Equal(HttpStatusCode.NotFound, byStranger.StatusCode);
        Assert.Equal(100_000, await harness.BalanceAsync(holder.Id));
    }

    /// <summary>US-9.19: pay less than face value, receive the face value.</summary>
    [Fact]
    public async Task A_voucher_purchase_through_the_d3_path_credits_the_face_value()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var buyer = await harness.Seed.DriverAsync();

        using var response = await harness.PostAsync(
            "/v1/vouchers/purchase",
            new { denominationMinor = 100_000, method = "onepay" },
            buyer.Bearer);

        var text = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"voucher purchase returned {(int)response.StatusCode}: {text}");

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;

        // The seeded 10% rung: pay Rs 900, receive Rs 1,000 of credit.
        Assert.Equal(100_000, root.GetProperty("denominationMinor").GetInt64());
        Assert.Equal(90_000, root.GetProperty("paidMinor").GetInt64());
        Assert.Equal(100_000, root.GetProperty("creditedMinor").GetInt64());

        Assert.Equal(100_000, await harness.BalanceAsync(buyer.Id));
    }

    /// <summary>Every forwarded route demands a bearer, and it is the caller's own.</summary>
    [Fact]
    public async Task The_forwarded_routes_demand_a_bearer()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        using var transfer = await harness.PostAsync(
            "/v1/transfers/driver", new { recipientDriverId = Guid.NewGuid().ToString(), amountMinor = 1_000 });

        using var pending = await harness.GetAsync("/v1/subscriptions/credit-transfer/pending");

        Assert.Equal(HttpStatusCode.Unauthorized, transfer.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, pending.StatusCode);
    }

    /// <summary>
    /// With wallet-svc unconfigured the D3' spellings are unmapped rather than answering 503 forever:
    /// the operations still exist under <c>/v1/wallet/**</c>, which is where they are implemented.
    /// </summary>
    [Fact]
    public async Task The_forwarded_routes_are_unmapped_when_wallet_svc_is_not_configured()
    {
        await using var harness = await SubscriptionHarness.StartAsync(
            postgres,
            redis,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Subscription:WalletBaseUrl"] = null,
            });

        var driver = await harness.Seed.DriverAsync();

        using var response = await harness.GetAsync("/v1/subscriptions/credit-transfer/pending", driver.Bearer);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
