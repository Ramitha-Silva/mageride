using System.Net;
using System.Net.Http.Json;
using Dapper;
using MageRide.Wallet.Endpoints;
using MageRide.Wallet.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Wallet.Tests.Integration;

/// <summary>
/// This component's fourth definition of done: <b>a replayed OnePay webhook (same
/// <c>provider_transaction_id</c>) credits the wallet only once</b> (R-19, D6' §7.1) — and its fifth:
/// <b>no bank-transfer endpoint or table is referenced anywhere</b> (AL-05).
/// </summary>
[Collection<WalletCollection>]
public sealed class TopupTests(PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
{
    /// <summary>
    /// AL-15's LankaQR rail, end to end: a deep link out, a signed confirm back, a credited wallet.
    /// </summary>
    /// <remarks>
    /// LankaQR rather than OnePay for the happy path, because it needs no outbound gateway call — the deep
    /// link is composed from the deployment's own template — so the test exercises this service and not a
    /// stub of somebody else's API.
    /// </remarks>
    [Fact]
    public async Task A_lankaqr_topup_returns_a_deep_link_and_credits_on_the_confirm()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync();

        using var started = await harness.PostAsync(
            "/v1/wallet/topup/lankaqr", new { amountMinor = 250_000 }, driver.Bearer);

        Assert.Equal(HttpStatusCode.OK, started.StatusCode);

        var topup = (await started.Content.ReadFromJsonAsync<TopupResponse>())!;

        Assert.Equal("Pending", topup.State);
        Assert.Equal(250_000, topup.AmountMinor);

        // AL-15: the bank-app deep link is the primary path. The QR is the fallback, and this deployment
        // configures no payload template, so it is absent rather than invented.
        Assert.StartsWith("combank://pay?ref=", topup.PaymentLink);
        Assert.Contains("amount=250000", topup.PaymentLink);
        Assert.Null(topup.QrPayload);

        // Nothing is credited by starting a session.
        Assert.Equal(0, await harness.BalanceAsync(driver.Id));

        using var confirmed = await harness.PostSignedCallbackAsync(
            "/v1/wallet/topup/lankaqr/confirm",
            new
            {
                providerTransactionId = "combank-txn-1",
                topupId = topup.TopupId,
                status = "SUCCESS",
                amountMinor = 250_000,
            },
            WalletHarness.LankaQrWebhookSecret);

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        Assert.Equal(250_000, await harness.BalanceAsync(driver.Id));

        // The D-08 cache carries the new balance for dispatch-svc's gate.
        Assert.Equal(250_000, await harness.CachedBalanceAsync(driver.Id));

        var polled = await harness.GetAsync<TopupResponse>(
            $"/v1/wallet/topup/{topup.TopupId}", driver.Bearer);

        Assert.Equal("Succeeded", polled.State);
    }

    /// <summary>The DoD, exactly: the same provider transaction id credits once.</summary>
    [Fact]
    public async Task A_replayed_callback_credits_the_wallet_only_once()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync();
        var topup = await StartLankaQrAsync(harness, driver, 100_000);

        var callback = new
        {
            providerTransactionId = "combank-txn-replayed",
            topupId = topup.TopupId,
            status = "SUCCESS",
            amountMinor = 100_000,
        };

        using var first = await harness.PostSignedCallbackAsync(
            "/v1/wallet/topup/lankaqr/confirm", callback, WalletHarness.LankaQrWebhookSecret);
        using var second = await harness.PostSignedCallbackAsync(
            "/v1/wallet/topup/lankaqr/confirm", callback, WalletHarness.LankaQrWebhookSecret);
        using var third = await harness.PostSignedCallbackAsync(
            "/v1/wallet/topup/lankaqr/confirm", callback, WalletHarness.LankaQrWebhookSecret);

        // Every redelivery answers 200 with the same body — which is what stops a provider retrying for
        // ever, and the contract says so explicitly.
        foreach (var response in new[] { first, second, third })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(
                """{"received":true}""",
                (await response.Content.ReadAsStringAsync()).Replace(" ", string.Empty, StringComparison.Ordinal));
        }

        Assert.Equal(100_000, await harness.BalanceAsync(driver.Id));
        Assert.Equal(1, await harness.EntryCountAsync("topup"));

        // One history line, and one settled session.
        await using var connection = await harness.OpenAsync();

        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "SELECT count(*)::int FROM billing.wallet_transactions WHERE kind = 'topup';"));
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "SELECT count(*)::int FROM billing.topups WHERE state = 'Succeeded';"));
    }

    /// <summary>An unsigned or wrongly signed callback credits nothing. There is no unsigned mode.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("deadbeef")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task A_callback_that_is_not_correctly_signed_is_refused(string? signature)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync();
        var topup = await StartLankaQrAsync(harness, driver, 100_000);

        using var response = await harness.PostSignedCallbackAsync(
            "/v1/wallet/topup/lankaqr/confirm",
            new
            {
                providerTransactionId = "combank-txn-unsigned",
                topupId = topup.TopupId,
                status = "SUCCESS",
                amountMinor = 100_000,
            },
            WalletHarness.LankaQrWebhookSecret,
            signatureOverride: signature ?? string.Empty);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await harness.BalanceAsync(driver.Id));
        Assert.Equal(0, await harness.EntryCountAsync("topup"));
    }

    /// <summary>
    /// A signature made with the *other* rail's secret is refused: the two rails have different secrets
    /// precisely so that neither can settle the other's money.
    /// </summary>
    [Fact]
    public async Task A_callback_signed_with_the_other_rails_secret_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync();
        var topup = await StartLankaQrAsync(harness, driver, 100_000);

        using var response = await harness.PostSignedCallbackAsync(
            "/v1/wallet/topup/lankaqr/confirm",
            new { providerTransactionId = "x", topupId = topup.TopupId, status = "SUCCESS" },
            WalletHarness.OnepayWebhookSecret);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await harness.BalanceAsync(driver.Id));
    }

    /// <summary>
    /// A callback whose amount disagrees with its session credits nothing in either direction, and leaves
    /// the session open for Finance (D6' §7.2).
    /// </summary>
    [Fact]
    public async Task A_callback_whose_amount_disagrees_credits_nothing()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync();
        var topup = await StartLankaQrAsync(harness, driver, 100_000);

        using var response = await harness.PostSignedCallbackAsync(
            "/v1/wallet/topup/lankaqr/confirm",
            new
            {
                providerTransactionId = "combank-txn-mismatch",
                topupId = topup.TopupId,
                status = "SUCCESS",
                amountMinor = 999_999,
            },
            WalletHarness.LankaQrWebhookSecret);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await harness.BalanceAsync(driver.Id));

        await using var connection = await harness.OpenAsync();

        Assert.Equal(
            "Pending",
            await connection.ExecuteScalarAsync<string>(
                "SELECT state FROM billing.topups WHERE id = @Id;", new { Id = topup.TopupId }));
    }

    /// <summary>A FAILED callback closes the session and credits nothing.</summary>
    [Fact]
    public async Task A_failed_callback_fails_the_session()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync();
        var topup = await StartLankaQrAsync(harness, driver, 100_000);

        using var response = await harness.PostSignedCallbackAsync(
            "/v1/wallet/topup/lankaqr/confirm",
            new
            {
                providerTransactionId = "combank-txn-failed",
                topupId = topup.TopupId,
                status = "FAILED",
                amountMinor = 100_000,
            },
            WalletHarness.LankaQrWebhookSecret);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await harness.BalanceAsync(driver.Id));

        var polled = await harness.GetAsync<TopupResponse>(
            $"/v1/wallet/topup/{topup.TopupId}", driver.Bearer);

        Assert.Equal("Failed", polled.State);
    }

    /// <summary>An amount outside the configured bounds never reaches a gateway.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(100_000_000)]
    public async Task An_amount_outside_the_bounds_is_refused(long amountMinor)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync();

        using var response = await harness.PostAsync(
            "/v1/wallet/topup/lankaqr", new { amountMinor }, driver.Bearer);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid-amount", (await WalletHarness.ProblemAsync(response)).Code);
    }

    /// <summary>
    /// The card rail answers <c>503</c> when OnePay is not configured, rather than pretending to have
    /// opened a session — and AL-05 leaves no third rail to fall back to.
    /// </summary>
    [Fact]
    public async Task The_card_rail_is_unavailable_rather_than_silent_when_unconfigured()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync();

        using var response = await harness.PostAsync(
            "/v1/wallet/topup/onepay", new { amountMinor = 100_000 }, driver.Bearer);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        // And the session it opened is not left Pending for a reconciler to puzzle over.
        await using var connection = await harness.OpenAsync();

        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "SELECT count(*)::int FROM billing.topups WHERE state = 'Pending';"));
    }

    /// <summary>
    /// The fifth definition of done, asserted against the running service: there is no bank-transfer
    /// surface, and the database will not hold one.
    /// </summary>
    [Theory]
    [InlineData("/v1/wallet/topup/bank-transfer")]
    [InlineData("/v1/wallet/topup/banktransfer")]
    [InlineData("/v1/wallet/admin/bank-transfers")]
    public async Task There_is_no_bank_transfer_endpoint(string path)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync();

        using var response = await harness.PostAsync(path, new { amountMinor = 100_000 }, driver.Bearer);

        // 404 where nothing matches, 405 where the path collides with the `GET /v1/wallet/topup/{topupId}`
        // template — which is itself the point: there is no POST anywhere on this service that would take
        // a bank transfer, and no route to add one to.
        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"POST {path} answered {(int)response.StatusCode}; a bank-transfer top-up must not be routable.");

        await using var connection = await harness.OpenAsync();

        Assert.Equal(
            0, await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM billing.topups;"));
    }

    /// <summary>
    /// And no table for it either — <c>ck_topups_method</c> refuses the row, so AL-05 is held by the
    /// database rather than by a review.
    /// </summary>
    [Fact]
    public async Task The_database_refuses_a_bank_transfer_top_up()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var driver = await harness.CreateDriverAsync(openingBalanceMinor: 1_000);

        await using var connection = await harness.OpenAsync();

        var accountId = await connection.ExecuteScalarAsync<Guid>(
            "SELECT id FROM billing.accounts WHERE owner_type='driver' AND owner_id = @Id;",
            new { Id = driver.Id });

        var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(
            async () => await connection.ExecuteAsync(
                """
                INSERT INTO billing.topups (driver_id, account_id, method, amount_minor)
                VALUES (@DriverId, @AccountId, 'bank_transfer', 100000);
                """,
                new { DriverId = driver.Id, AccountId = accountId }));

        Assert.Equal("ck_topups_method", exception.ConstraintName);
    }

    /// <summary>A top-up session belongs to its driver; nobody else can read it.</summary>
    [Fact]
    public async Task Another_drivers_topup_is_not_found()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await WalletHarness.StartAsync(postgres, redis, redpanda);

        var mine = await harness.CreateDriverAsync();
        var theirs = await harness.CreateDriverAsync();

        var topup = await StartLankaQrAsync(harness, mine, 100_000);

        using var response = await harness.GetAsync($"/v1/wallet/topup/{topup.TopupId}", theirs.Bearer);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<TopupResponse> StartLankaQrAsync(
        WalletHarness harness, SeededDriver driver, long amountMinor)
    {
        using var response = await harness.PostAsync(
            "/v1/wallet/topup/lankaqr", new { amountMinor }, driver.Bearer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<TopupResponse>())!;
    }
}
