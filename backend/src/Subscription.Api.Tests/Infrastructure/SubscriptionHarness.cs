using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using MageRide.Shared.Http;
using MageRide.Subscriptions.Endpoints;
using MageRide.TestKit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace MageRide.Subscriptions.Tests.Infrastructure;

/// <summary>
/// A running subscription-svc and a running wallet-svc on real sockets, against a real Postgres and a
/// real Redis.
/// </summary>
/// <remarks>
/// <para>
/// Both are built through their own composition roots, so the pipelines under test — the bearer
/// handler, the problem+json handler, the idempotency middleware, the two internal-key filters, the
/// resilience pipeline on the ledger seam — are the ones the processes run.
/// </para>
/// <para>
/// <b>wallet-svc is real, not stubbed.</b> The definition of done's first line is "charging twice on
/// the same (driver, vehicle, Asia/Colombo date) debits once", and half of that guarantee is a UNIQUE
/// index in another service's schema. A stub would assert this suite's own arithmetic.
/// </para>
/// <para>
/// <b>The clock is a <see cref="FakeTimeProvider"/> shared by both services</b>, so a Colombo day
/// boundary is a fact a test can state rather than wait for — and both services agree on what day it
/// is, which is the whole subject.
/// </para>
/// </remarks>
internal sealed class SubscriptionHarness : IAsyncDisposable
{
    /// <summary>The interim shared secret subscription-svc's internal fee plane demands.</summary>
    public const string InternalApiKey = "c047-subscription-internal-key-not-a-secret";

    /// <summary>The one wallet-svc's internal ledger plane demands.</summary>
    public const string WalletInternalApiKey = "c047-wallet-internal-key-not-a-secret";

    /// <summary>The HMAC secrets the two Mode B provider callbacks are signed with (Δ C048).</summary>
    public const string OnepayWebhookSecret = "c048-onepay-webhook-secret-not-a-secret";

    public const string LankaQrWebhookSecret = "c048-lankaqr-webhook-secret-not-a-secret";

    /// <summary>Signs the expiring URLs on <c>payTo.lankaqrImageUrl</c> and <c>slipUrl</c>.</summary>
    public const string FileLinkSigningKey = "c048-file-link-signing-key-not-a-secret";

    /// <summary>
    /// 09:00 UTC on 30 July 2026 — 14:30 in Colombo, comfortably mid-day so a test that adds a few
    /// hours does not cross a business-date boundary by accident.
    /// </summary>
    public static readonly DateTimeOffset DefaultNow = new(2026, 7, 30, 9, 0, 0, TimeSpan.Zero);

    private readonly WebApplication _subscriptions;
    private readonly WebApplication _wallet;
    private readonly PostgresFixture _postgres;

    private SubscriptionHarness(
        WebApplication subscriptions,
        WebApplication wallet,
        PostgresFixture postgres,
        TestTokenIssuer tokens,
        FakeTimeProvider clock)
    {
        _subscriptions = subscriptions;
        _wallet = wallet;
        _postgres = postgres;
        Tokens = tokens;
        Clock = clock;

        Client = new HttpClient { BaseAddress = new Uri(AddressOf(subscriptions)), Timeout = TimeSpan.FromSeconds(120) };
        Wallet = new HttpClient { BaseAddress = new Uri(AddressOf(wallet)), Timeout = TimeSpan.FromSeconds(120) };
        Seed = new SubscriptionSeed(postgres, this);
    }

    public HttpClient Client { get; }

    /// <summary>wallet-svc directly — for seeding a balance the way an admin adjustment would.</summary>
    public HttpClient Wallet { get; }

    public TestTokenIssuer Tokens { get; }

    public FakeTimeProvider Clock { get; }

    public SubscriptionSeed Seed { get; }

    public static async Task<SubscriptionHarness> StartAsync(
        PostgresFixture postgres,
        RedisFixture redis,
        IDictionary<string, string?>? settings = null,
        DateTimeOffset? now = null,
        bool startWallet = true)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        ArgumentNullException.ThrowIfNull(redis);

        postgres.RequireAvailable();
        redis.RequireAvailable();
        await postgres.EnsureMigratedAsync();
        await ResetAsync(postgres);

        var tokens = new TestTokenIssuer();
        var clock = new FakeTimeProvider(now ?? DefaultNow);

        // One slip directory per harness, so a transfer screenshot written by one test cannot be
        // read back by another that happened to mint the same payment id.
        var slipRoot = Path.Combine(Path.GetTempPath(), "mageride-c048", Guid.NewGuid().ToString("N"));

        var wallet = BuildWallet(postgres, redis, tokens, clock);
        await wallet.StartAsync();

        var walletAddress = AddressOf(wallet);

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            ["Postgres:PgBouncerTransactionMode"] = "false",
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = tokens.IssuerName,
            ["Jwt:RequireHttpsMetadata"] = "false",
            ["Subscription:InternalApiKey"] = InternalApiKey,
            ["Subscription:WalletBaseUrl"] = startWallet ? walletAddress : null,
            ["Subscription:WalletInternalApiKey"] = startWallet ? WalletInternalApiKey : null,
            // Off by default: a background run raising rows underneath a Mode B assertion makes
            // "the endpoint raised them" indistinguishable from "the runner did". The test that
            // asserts the runner turns it on.
            ["Subscription:ModeBBillingEnabled"] = "false",
            ["Subscription:OnepayWebhookSecret"] = OnepayWebhookSecret,
            ["Subscription:LankaQrWebhookSecret"] = LankaQrWebhookSecret,
            ["Subscription:FileLinkSigningKey"] = FileLinkSigningKey,
            ["Subscription:SlipRoot"] = slipRoot,
            // Δ C048: Epic 23 gives this service an outbox, so it needs a broker address to
            // validate. Pointed at a dead port and the dispatcher switched off, the same fallback
            // WalletHarness takes — the D-22 assertions are about the `subscription.outbox` row
            // committing with the grant change, which is the half this component owns. What
            // fanout-svc does with it afterwards is C041's suite.
            ["Kafka:BootstrapServers"] = "127.0.0.1:1",
            ["Outbox:DispatcherEnabled"] = "false",
            ["urls"] = "http://127.0.0.1:0",
            // One /metrics endpoint per harness would collide across concurrently running tests.
            ["Otel:PrometheusEnabled"] = "false",
        };

        if (settings is not null)
        {
            foreach (var (key, value) in settings)
            {
                overrides[key] = value;
            }
        }

        var subscriptions = SubscriptionApplication.Build(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = AppContext.BaseDirectory,
            },
            builder =>
            {
                Quieten(builder);
                builder.Configuration.AddInMemoryCollection(overrides);
                builder.Services.AddSingleton<TimeProvider>(clock);
                UseTestSigningKey(builder, tokens);
            });

        await subscriptions.StartAsync();

        return new SubscriptionHarness(subscriptions, wallet, postgres, tokens, clock);
    }

    // -----------------------------------------------------------------------------------------
    // HTTP
    // -----------------------------------------------------------------------------------------

    public async Task<HttpResponseMessage> GetAsync(string path, string? bearer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return await Client.SendAsync(request);
    }

    public async Task<T> GetAsync<T>(string path, string? bearer = null)
    {
        using var response = await GetAsync(path, bearer);
        var text = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == System.Net.HttpStatusCode.OK,
            $"GET {path} returned {(int)response.StatusCode}: {text}");

        return JsonSerializer.Deserialize<T>(text, MageRideJson.Options)!;
    }

    public Task<HttpResponseMessage> PostAsync(
        string path, object? body, string? bearer = null, string? internalKey = null) =>
        PostWithKeyAsync(path, body, bearer, Guid.NewGuid().ToString(), internalKey);

    public async Task<HttpResponseMessage> PostWithKeyAsync(
        string path, object? body, string? bearer, string? idempotencyKey, string? internalKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (internalKey is not null)
        {
            request.Headers.TryAddWithoutValidation(InternalKeyFilter.ApiKeyHeader, internalKey);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation(MageRideHeaders.IdempotencyKey, idempotencyKey);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: MageRideJson.Options);
        }

        return await Client.SendAsync(request);
    }

    public async Task<HttpResponseMessage> PutAsync(string path, object body, string bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = JsonContent.Create(body, options: MageRideJson.Options),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        return await Client.SendAsync(request);
    }

    public async Task<HttpResponseMessage> DeleteAsync(string path, string bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        return await Client.SendAsync(request);
    }

    /// <summary>Asserts a 2xx and deserialises. Every Mode B assertion goes through this.</summary>
    public async Task<T> OkAsync<T>(HttpResponseMessage response, string what)
    {
        ArgumentNullException.ThrowIfNull(response);

        var text = await response.Content.ReadAsStringAsync();

        Assert.True(
            (int)response.StatusCode is >= 200 and < 300,
            $"{what} returned {(int)response.StatusCode}: {text}");

        response.Dispose();

        return JsonSerializer.Deserialize<T>(text, MageRideJson.Options)!;
    }

    /// <summary>The multipart upload <c>POST .../transfer-slip</c> takes.</summary>
    public async Task<HttpResponseMessage> PostFileAsync(
        string path, string bearer, byte[] bytes, string fileName = "slip.png")
    {
        ArgumentNullException.ThrowIfNull(bytes);

        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.TryAddWithoutValidation(MageRideHeaders.IdempotencyKey, Guid.NewGuid().ToString());

        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "file", fileName);
        request.Content = form;

        return await Client.SendAsync(request);
    }

    /// <summary>
    /// A provider callback, signed the way the gateway would (HMAC-SHA256 over the raw body).
    /// </summary>
    public async Task<HttpResponseMessage> PostSignedAsync(string path, object body, string secret)
    {
        var raw = JsonSerializer.SerializeToUtf8Bytes(body, MageRideJson.Options);

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(raw),
        };

        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation(
            MageRide.Shared.Payments.WebhookSignature.HeaderName,
            MageRide.Shared.Payments.WebhookSignature.Compute(raw, secret));

        return await Client.SendAsync(request);
    }

    /// <summary>The events this service queued on <c>subscription.outbox</c> for one vehicle.</summary>
    public async Task<IReadOnlyList<(string EventType, string Payload)>> OutboxAsync(Guid vehicleId)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<(string EventType, string Payload)>(
            """
            SELECT event_type, payload::text AS payload
              FROM subscription.outbox
             WHERE aggregate_id = @VehicleId
             ORDER BY id;
            """,
            new { VehicleId = vehicleId });

        return [.. rows];
    }

    /// <summary>A subscription's stored next-due date, straight from the row.</summary>
    public async Task<DateOnly?> NextDueAsync(Guid subscriptionId)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<DateOnly?>(
            "SELECT next_due FROM subscription.subscriptions WHERE id = @Id;", new { Id = subscriptionId });
    }

    /// <summary>A grant's lifecycle columns — the AL-25 "muted until deleted" assertions read these.</summary>
    public async Task<(string Status, bool Deleted)?> GrantAsync(Guid grantId)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<(string Status, bool Deleted)>(
            "SELECT status, (deleted_at IS NOT NULL) AS deleted FROM subscription.grants WHERE id = @Id;",
            new { Id = grantId });

        return rows.Cast<(string Status, bool Deleted)?>().FirstOrDefault();
    }

    /// <summary>Charges the daily fee the way ride-svc does, through the internal plane.</summary>
    public Task<HttpResponseMessage> ChargeAsync(Guid driverId, Guid vehicleId, Guid? rideId = null) =>
        PostAsync(
            $"/v1/internal/fees/{driverId}/charge-before-trip",
            new { vehicleId = vehicleId.ToString(), rideId = rideId?.ToString() },
            internalKey: InternalApiKey);

    /// <summary>The same call, asserted to have succeeded, deserialised.</summary>
    public async Task<DailyFeeChargeResponse> ChargeOkAsync(Guid driverId, Guid vehicleId, Guid? rideId = null)
    {
        using var response = await ChargeAsync(driverId, vehicleId, rideId);
        var text = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == System.Net.HttpStatusCode.OK,
            $"charge-before-trip returned {(int)response.StatusCode}: {text}");

        return JsonSerializer.Deserialize<DailyFeeChargeResponse>(text, MageRideJson.Options)!;
    }

    /// <summary>The RFC 7807 <c>type</c> code and body of a failed response.</summary>
    public static async Task<(string Code, JsonElement Body)> ProblemAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var text = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement.Clone();

        var type = root.TryGetProperty("type", out var value) ? value.GetString() ?? string.Empty : string.Empty;

        return (type.Split('/')[^1], root);
    }

    // -----------------------------------------------------------------------------------------
    // Asserting against the ledger and the fee rows
    // -----------------------------------------------------------------------------------------

    public Task<NpgsqlConnection> OpenAsync() => _postgres.OpenAsync();

    /// <summary>A driver's balance, from <c>billing.accounts</c> — the master (§10).</summary>
    public async Task<long> BalanceAsync(Guid driverId)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<long?>(
            """
            SELECT balance_minor FROM billing.accounts
             WHERE owner_type = 'driver' AND owner_id = @DriverId AND currency = 'LKR';
            """,
            new { DriverId = driverId }) ?? 0;
    }

    /// <summary>How many journal entries of one kind exist. "Never charged" is asserted with this.</summary>
    public async Task<int> EntryCountAsync(string kind)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM billing.journal_entries WHERE kind = @Kind;", new { Kind = kind });
    }

    /// <summary>Entries whose idempotency key is exactly the D-13 spelling for one driver-day.</summary>
    public async Task<int> DailyFeeEntryCountAsync(Guid driverId, Guid vehicleId, DateOnly feeDate)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM billing.journal_entries WHERE idempotency_key = @Key;",
            new { Key = $"daily_fee:{driverId}:{vehicleId}:{feeDate:yyyy-MM-dd}" });
    }

    /// <summary>Σ of every posting in the ledger. Must be zero, always, whatever happened.</summary>
    public async Task<long> LedgerSumAsync()
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<long>(
            "SELECT coalesce(sum(amount_minor), 0) FROM billing.journal_postings;");
    }

    /// <summary>Charge rows for one driver, oldest first.</summary>
    public async Task<IReadOnlyList<(Guid VehicleId, DateOnly FeeDate, long AmountMinor, string Status)>>
        ChargeRowsAsync(Guid driverId)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<(Guid VehicleId, DateOnly FeeDate, long AmountMinor, string Status)>(
            """
            SELECT vehicle_id, fee_date, amount_minor, status
              FROM billing.daily_fee_charges
             WHERE driver_id = @DriverId
             ORDER BY fee_date, charged_at;
            """,
            new { DriverId = driverId });

        return [.. rows];
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        Wallet.Dispose();

        await StopAsync(_subscriptions);
        await StopAsync(_wallet);
    }

    // -----------------------------------------------------------------------------------------
    // Composition
    // -----------------------------------------------------------------------------------------

    /// <remarks>
    /// Kafka is pointed at a dead address and the outbox dispatcher is off — the same fallback
    /// <c>WalletHarness</c> takes when Redpanda is unavailable. This suite asserts on the ledger, not
    /// on <c>wallet.events</c>, so a broker would be a third container for nothing.
    /// </remarks>
    private static WebApplication BuildWallet(
        PostgresFixture postgres, RedisFixture redis, TestTokenIssuer tokens, FakeTimeProvider clock) =>
        MageRide.Wallet.WalletApplication.Build(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = AppContext.BaseDirectory,
            },
            builder =>
            {
                Quieten(builder);

                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
                    ["Postgres:PgBouncerTransactionMode"] = "false",
                    ["ConnectionStrings:Redis"] = redis.ConnectionString,
                    ["Kafka:BootstrapServers"] = "127.0.0.1:1",
                    ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
                    ["Jwt:Issuer"] = tokens.IssuerName,
                    ["Jwt:RequireHttpsMetadata"] = "false",
                    ["Wallet:InternalApiKey"] = WalletInternalApiKey,
                    ["urls"] = "http://127.0.0.1:0",
                    ["Otel:PrometheusEnabled"] = "false",
                    ["Outbox:DispatcherEnabled"] = "false",
                });

                builder.Services.AddSingleton<TimeProvider>(clock);
                UseTestSigningKey(builder, tokens);
            });

    private static void Quieten(WebApplicationBuilder builder)
    {
        // MAGERIDE_TEST_LOGS=1 keeps the console provider when a failure needs a trace.
        if (Environment.GetEnvironmentVariable("MAGERIDE_TEST_LOGS") != "1")
        {
            builder.Logging.ClearProviders();
        }
    }

    /// <remarks>PostConfigure, so this runs after the kernel's AddMageRideAuth has built the options.</remarks>
    private static void UseTestSigningKey(WebApplicationBuilder builder, TestTokenIssuer tokens) =>
        builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .PostConfigure(bearer =>
            {
                bearer.ConfigurationManager = null;
                bearer.TokenValidationParameters.IssuerSigningKey = tokens.PublicKey;
                bearer.TokenValidationParameters.IssuerSigningKeyResolver = null;
            });

    private static string AddressOf(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

    private static async Task StopAsync(WebApplication app)
    {
        try
        {
            await app.StopAsync(TimeSpan.FromSeconds(10));
            await app.DisposeAsync();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"warning: could not stop a harness service: {exception.Message}");
        }
    }

    /// <summary>
    /// Empties what these two services own, and restores the reference data 1901 seeds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The TestKit shares one container per collection and does not reset between tests, and several
    /// assertions here are about the whole table — "the ledger sums to zero" and "no <c>daily_fee</c>
    /// entry exists" are claims about every row, so a posting another test left behind would be part of
    /// this test's answer.
    /// </para>
    /// <para>
    /// <b><c>billing.plans</c> and <c>billing.voucher_discount_tiers</c> are restored rather than
    /// truncated.</b> They are 1901's reference data, this component's admin routes edit them, and a
    /// suite that left an edited ladder behind would make the next test's rate depend on the order the
    /// runner picked. Restoring is also how the "no retro-billing" test can safely change a rate.
    /// </para>
    /// <para>
    /// <b><c>registry.vehicles</c> and <c>rides.rides</c> are cleared, and they have to be.</b> The
    /// Mode B run is a set operation over <em>every</em> approved Mode B vehicle, so one left behind by
    /// an earlier test becomes part of the next test's <c>raised</c> count — a contamination that shows
    /// up as an order-dependent failure rather than as a wrong number. <c>CASCADE</c> because both are
    /// referenced across half the schema and this suite owns none of those rows.
    /// </para>
    /// <para>
    /// <c>iam.users</c> is deliberately <b>not</b> cleared: every test seeds fresh ULIDs, and
    /// truncating it would cascade into <c>billing.voucher_discount_tiers.updated_by</c> and take
    /// 1901's ladder with it.
    /// </para>
    /// </remarks>
    private static async Task ResetAsync(PostgresFixture postgres)
    {
        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            TRUNCATE rides.rides, registry.vehicles, registry.fleets CASCADE;

            -- Named rather than left to the CASCADE above, so a schema change that drops one of
            -- these foreign keys shows up here as contamination rather than as a mystery.
            TRUNCATE subscription.payments, subscription.subscriptions, subscription.grants,
                     subscription.access_requests, subscription.outbox, docs.uploads CASCADE;

            -- support.ticket_events is named for the same reason, and it is not optional: C053's
            -- migration 1309 gave it a foreign key onto support.tickets, and Postgres refuses to
            -- truncate a referenced table unless the referencing one goes with it. Naming it
            -- rather than adding CASCADE keeps this list's own property — if that FK is ever
            -- dropped, the thread is still emptied instead of silently surviving the reset.
            -- billing.fleet_invoice_lines and billing.fleet_topups are the same case, arriving from
            -- C060's migration 1108: a line points at the billing.monthly_subscriptions charge it
            -- consolidated (which is what makes one raised charge reachable from exactly one
            -- invoice), and a top-up session points at the billing.accounts row it credits.
            TRUNCATE billing.daily_fee_charges, billing.monthly_subscriptions, billing.fleet_invoices,
                     billing.fleet_invoice_lines, billing.fleet_topups, billing.fleet_outbox,
                     billing.journal_postings, billing.wallet_transactions, billing.outbox,
                     billing.command_log, billing.topups, billing.voucher_purchases,
                     billing.credit_transfers, subscription.command_log,
                     support.ticket_events, support.tickets;
            DELETE FROM billing.journal_entries;
            DELETE FROM billing.wallets;
            DELETE FROM billing.accounts WHERE owner_type IN ('driver','fleet');
            UPDATE billing.accounts SET balance_minor = 0 WHERE owner_type IN ('platform','suspense');

            DELETE FROM billing.plans
             WHERE vehicle_type NOT IN
               ('bus','train','motorbike','three_wheeler','flex','sedan','mini_van','van');

            INSERT INTO billing.plans (vehicle_type, daily_fee_minor, mode) VALUES
              ('bus', 0, 'A'), ('train', 0, 'A'), ('motorbike', 5000, 'C'),
              ('three_wheeler', 10000, 'C'), ('flex', 15000, 'C'), ('sedan', 20000, 'C'),
              ('mini_van', 25000, 'C'), ('van', 30000, 'C')
            ON CONFLICT (vehicle_type) DO UPDATE
               SET daily_fee_minor = EXCLUDED.daily_fee_minor, mode = EXCLUDED.mode;

            DELETE FROM billing.voucher_discount_tiers
             WHERE denomination_minor NOT IN (100000, 200000, 300000, 500000, 1000000);

            INSERT INTO billing.voucher_discount_tiers (denomination_minor, discount_bps) VALUES
              (100000, 1000), (200000, 1100), (300000, 1200), (500000, 1300), (1000000, 1500)
            ON CONFLICT (denomination_minor) DO UPDATE
               SET discount_bps = EXCLUDED.discount_bps, active = true, updated_by = NULL;
            """);
    }
}
