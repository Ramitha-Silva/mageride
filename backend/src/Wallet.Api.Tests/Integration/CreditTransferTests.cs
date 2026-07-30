using System.Net;
using System.Net.Http.Json;
using Dapper;
using MageRide.Shared.Primitives;
using MageRide.Wallet.Endpoints;
using MageRide.Wallet.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Wallet.Tests.Integration;

/// <summary>
/// This component's third definition of done: <b>a credit transfer of X debits the sender X and credits
/// the recipient X — no fee row is ever written</b> (US-9.13/9.21, AL-01).
/// </summary>
[Collection<WalletCollection>]
public sealed class CreditTransferTests(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
{
    /// <summary>A proactive send (US-9A.12): exact value, two legs, no third row anywhere.</summary>
    [Fact]
    public async Task A_direct_send_moves_the_exact_value_and_writes_no_fee()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var sender = await harness.CreateDriverAsync(openingBalanceMinor: 100_000);
        var recipient = await harness.CreateDriverAsync();

        using var response = await harness.PostAsync(
            "/v1/wallet/credit-transfer/initiate",
            new { recipientDriverId = recipient.Id, amountMinor = 40_000 },
            sender.Bearer);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var transfer = (await response.Content.ReadFromJsonAsync<TransferResponse>())!;

        Assert.Equal("sent", transfer.Direction);
        Assert.Equal("APPROVED", transfer.Status);
        Assert.Equal(40_000, transfer.AmountMinor);
        Assert.Equal(recipient.Id, transfer.CounterpartyDriverId);

        // Exactly X out and exactly X in.
        Assert.Equal(60_000, await harness.BalanceAsync(sender.Id));
        Assert.Equal(40_000, await harness.BalanceAsync(recipient.Id));

        await using var connection = await harness.OpenAsync();

        var entryId = await connection.ExecuteScalarAsync<Guid>(
            "SELECT journal_entry_id FROM billing.credit_transfers WHERE id = @Id;",
            new { Id = transfer.TransferId });

        // **Two** legs. A third would be the commission AL-01 removed, and there is no journal kind that
        // could carry it — so this assertion is about the shape of the whole platform, not this call.
        var postings = await harness.PostingsAsync(entryId);

        Assert.Equal(2, postings.Count);
        Assert.Equal(0, postings.Sum(posting => posting.AmountMinor));
        Assert.Contains(postings, posting => posting.AmountMinor == -40_000);
        Assert.Contains(postings, posting => posting.AmountMinor == 40_000);

        // The platform account was not a party to it at all: this is money between two drivers.
        var platformTouched = await connection.ExecuteScalarAsync<int>(
            """
            SELECT count(*)::int FROM billing.journal_postings p
              JOIN billing.accounts a ON a.id = p.account_id
             WHERE p.entry_id = @EntryId AND a.owner_type <> 'driver';
            """,
            new { EntryId = entryId });

        Assert.Equal(0, platformTouched);
    }

    /// <summary>The requested flow end to end (US-9.10 → US-9A.10 → US-9.13).</summary>
    [Fact]
    public async Task A_request_is_visible_to_the_holder_then_approved_at_par()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var holder = await harness.CreateDriverAsync(openingBalanceMinor: 200_000);
        var requester = await harness.CreateDriverAsync();

        using var requested = await harness.PostAsync(
            "/v1/wallet/credit-transfer/request",
            new { holderDriverId = holder.Id, amountMinor = 75_000 },
            requester.Bearer);

        Assert.Equal(HttpStatusCode.Created, requested.StatusCode);

        var request = (await requested.Content.ReadFromJsonAsync<TransferResponse>())!;

        Assert.Equal("PENDING", request.Status);
        Assert.Equal("received", request.Direction);

        // Nothing has moved yet: the balance is checked when the holder answers, not when asked.
        Assert.Equal(200_000, await harness.BalanceAsync(holder.Id));
        Assert.Equal(0, await harness.BalanceAsync(requester.Id));

        // The holder's inbox, and only the holder's.
        var inbox = await harness.GetAsync<CursorPage<TransferResponse>>(
            "/v1/wallet/credit-transfer/pending", holder.Bearer);

        Assert.Equal(request.TransferId, Assert.Single(inbox.Items).TransferId);

        var requesterInbox = await harness.GetAsync<CursorPage<TransferResponse>>(
            "/v1/wallet/credit-transfer/pending", requester.Bearer);

        Assert.Empty(requesterInbox.Items);

        using var approved = await harness.PostAsync(
            $"/v1/wallet/credit-transfer/{request.TransferId}/approve", body: null, holder.Bearer);

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        var settled = (await approved.Content.ReadFromJsonAsync<TransferResponse>())!;

        Assert.Equal("APPROVED", settled.Status);
        Assert.Equal(125_000, await harness.BalanceAsync(holder.Id));
        Assert.Equal(75_000, await harness.BalanceAsync(requester.Id));

        // Both sides see it in their history, from their own point of view.
        var senderHistory = await harness.GetAsync<CursorPage<TransferResponse>>(
            $"/v1/wallet/{holder.Id}/transfers", holder.Bearer);
        var recipientHistory = await harness.GetAsync<CursorPage<TransferResponse>>(
            $"/v1/wallet/{requester.Id}/transfers", requester.Bearer);

        Assert.Equal("sent", Assert.Single(senderHistory.Items).Direction);
        Assert.Equal("received", Assert.Single(recipientHistory.Items).Direction);
    }

    /// <summary>Approving twice posts once; the second call is a conflict.</summary>
    [Fact]
    public async Task Approving_twice_moves_the_money_once()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var holder = await harness.CreateDriverAsync(openingBalanceMinor: 100_000);
        var requester = await harness.CreateDriverAsync();

        var request = await RequestAsync(harness, holder, requester, 30_000);

        using var first = await harness.PostAsync(
            $"/v1/wallet/credit-transfer/{request.TransferId}/approve", body: null, holder.Bearer);
        using var second = await harness.PostAsync(
            $"/v1/wallet/credit-transfer/{request.TransferId}/approve", body: null, holder.Bearer);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        Assert.Equal(70_000, await harness.BalanceAsync(holder.Id));
        Assert.Equal(30_000, await harness.BalanceAsync(requester.Id));
        Assert.Equal(1, await harness.EntryCountAsync("driver_transfer"));
    }

    /// <summary>A holder who cannot cover it at approval time is refused, and nothing is posted.</summary>
    [Fact]
    public async Task Approving_beyond_the_balance_is_refused_at_approval_time()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var holder = await harness.CreateDriverAsync(openingBalanceMinor: 50_000);
        var requester = await harness.CreateDriverAsync();

        // Asked for more than the holder has — allowed to *ask*, because the balance at request time says
        // nothing about the balance when they answer.
        var request = await RequestAsync(harness, holder, requester, 80_000);

        using var response = await harness.PostAsync(
            $"/v1/wallet/credit-transfer/{request.TransferId}/approve", body: null, holder.Bearer);

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        Assert.Equal("insufficient-wallet", (await WalletHarness.ProblemAsync(response)).Code);

        Assert.Equal(50_000, await harness.BalanceAsync(holder.Id));
        Assert.Equal(0, await harness.BalanceAsync(requester.Id));

        // Still pending, so the holder can top up and approve it later.
        await using var connection = await harness.OpenAsync();

        Assert.Equal(
            "PENDING",
            await connection.ExecuteScalarAsync<string>(
                "SELECT status FROM billing.credit_transfers WHERE id = @Id;",
                new { Id = request.TransferId }));
    }

    /// <summary>US-9.12 — a rejection posts nothing and cannot be approved afterwards.</summary>
    [Fact]
    public async Task A_rejected_request_posts_nothing_and_stays_rejected()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var holder = await harness.CreateDriverAsync(openingBalanceMinor: 100_000);
        var requester = await harness.CreateDriverAsync();

        var request = await RequestAsync(harness, holder, requester, 20_000);

        using var rejected = await harness.PostAsync(
            $"/v1/wallet/credit-transfer/{request.TransferId}/reject", body: null, holder.Bearer);

        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Equal("REJECTED", (await rejected.Content.ReadFromJsonAsync<TransferResponse>())!.Status);

        using var approveAfter = await harness.PostAsync(
            $"/v1/wallet/credit-transfer/{request.TransferId}/approve", body: null, holder.Bearer);

        Assert.Equal(HttpStatusCode.Conflict, approveAfter.StatusCode);

        Assert.Equal(100_000, await harness.BalanceAsync(holder.Id));
        Assert.Equal(0, await harness.EntryCountAsync("driver_transfer"));
    }

    /// <summary>
    /// Only the holder may answer, and a request that is not theirs is a 404 rather than a 403 — the
    /// house rule against membership oracles.
    /// </summary>
    [Fact]
    public async Task Somebody_elses_request_is_not_found()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var holder = await harness.CreateDriverAsync(openingBalanceMinor: 100_000);
        var requester = await harness.CreateDriverAsync();
        var stranger = await harness.CreateDriverAsync(openingBalanceMinor: 100_000);

        var request = await RequestAsync(harness, holder, requester, 10_000);

        foreach (var bearer in new[] { stranger.Bearer, requester.Bearer })
        {
            using var response = await harness.PostAsync(
                $"/v1/wallet/credit-transfer/{request.TransferId}/approve", body: null, bearer);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        Assert.Equal(0, await harness.EntryCountAsync("driver_transfer"));
    }

    /// <summary>A driver cannot transfer to themselves — it would post two legs and move nothing.</summary>
    [Fact]
    public async Task A_self_transfer_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync(openingBalanceMinor: 50_000);

        using var response = await harness.PostAsync(
            "/v1/wallet/credit-transfer/initiate",
            new { recipientDriverId = driver.Id, amountMinor = 1_000 },
            driver.Bearer);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(50_000, await harness.BalanceAsync(driver.Id));
    }

    /// <summary>
    /// The recipient must be a driver: AL-01 makes this a facility between ordinary driver accounts, and
    /// a passenger has no wallet to receive it.
    /// </summary>
    [Fact]
    public async Task A_recipient_who_is_not_a_driver_is_not_found()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var sender = await harness.CreateDriverAsync(openingBalanceMinor: 50_000);
        var passenger = await harness.CreateUserAsync("passenger");

        using var response = await harness.PostAsync(
            "/v1/wallet/credit-transfer/initiate",
            new { recipientDriverId = passenger, amountMinor = 1_000 },
            sender.Bearer);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(50_000, await harness.BalanceAsync(sender.Id));
    }

    /// <summary>
    /// AL-01: these are Driver-App APIs. An admin bearer is refused — the portal has no transfer screen
    /// and never had one.
    /// </summary>
    [Fact]
    public async Task The_transfer_surface_is_driver_only()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var recipient = await harness.CreateDriverAsync();

        using var byAdmin = await harness.PostAsync(
            "/v1/wallet/credit-transfer/initiate",
            new { recipientDriverId = recipient.Id, amountMinor = 1_000 },
            harness.Tokens.Admin(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, byAdmin.StatusCode);
    }

    private static async Task<TransferResponse> RequestAsync(
        WalletHarness harness, SeededDriver holder, SeededDriver requester, long amountMinor)
    {
        using var response = await harness.PostAsync(
            "/v1/wallet/credit-transfer/request",
            new { holderDriverId = holder.Id, amountMinor },
            requester.Bearer);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<TransferResponse>())!;
    }
}
