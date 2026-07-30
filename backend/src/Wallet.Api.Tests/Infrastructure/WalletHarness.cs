using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using MageRide.Shared.Caching;
using MageRide.Shared.Http;
using MageRide.Shared.Payments;
using MageRide.Wallet.Endpoints;
using MageRide.TestKit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using StackExchange.Redis;

namespace MageRide.Wallet.Tests.Infrastructure;

/// <summary>
/// A running wallet-svc on a real socket, against a real Postgres, a real Redis and a real Redpanda.
/// </summary>
/// <remarks>
/// <para>
/// Built through <see cref="WalletApplication.Build"/>, so the pipeline under test — the bearer
/// handler, the problem+json handler, the idempotency middleware, the outbox dispatcher, the internal-key
/// filter — is the one the process runs.
/// </para>
/// <para>
/// <b>The clock is a <see cref="FakeTimeProvider"/></b>, so a settlement instant and D6' §7.1's
/// 90-second window are facts a test can state rather than race.
/// </para>
/// <para>
/// <b>The gateways are pointed at this harness's own stub</b> for OnePay and at a template for LankaQR:
/// what these tests are about is the ledger and the callback, and a real gateway would make the suite
/// fail for somebody else's reasons.
/// </para>
/// </remarks>
internal sealed class WalletHarness : IAsyncDisposable
{
    /// <summary>The interim shared secret the internal ledger plane demands until the mesh lands.</summary>
    public const string InternalApiKey = "c046-wallet-internal-key-not-a-secret";

    /// <summary>The OnePay callback secret this harness signs with.</summary>
    public const string OnepayWebhookSecret = "c046-onepay-webhook-secret";

    /// <summary>The LankaQR / ComBank IPG callback secret.</summary>
    public const string LankaQrWebhookSecret = "c046-lankaqr-webhook-secret";

    private readonly WebApplication _app;
    private readonly PostgresFixture _postgres;

    private WalletHarness(
        WebApplication app,
        PostgresFixture postgres,
        RedisFixture redis,
        RedpandaFixture redpanda,
        TestTokenIssuer tokens,
        FakeTimeProvider clock)
    {
        _app = app;
        _postgres = postgres;
        Redis = redis;
        Redpanda = redpanda;
        Tokens = tokens;
        Clock = clock;

        // Fully qualified: StackExchange.Redis has an IServer too, and this suite uses both namespaces.
        var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        Client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(120) };
    }

    public HttpClient Client { get; }

    public TestTokenIssuer Tokens { get; }

    public FakeTimeProvider Clock { get; }

    public RedisFixture Redis { get; }

    public RedpandaFixture Redpanda { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<WalletHarness> StartAsync(
        PostgresFixture postgres,
        RedisFixture redis,
        RedpandaFixture redpanda,
        IDictionary<string, string?>? settings = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(redpanda);

        postgres.RequireAvailable();
        redis.RequireAvailable();
        await postgres.EnsureMigratedAsync();
        await ResetAsync(postgres);

        var tokens = new TestTokenIssuer();
        var clock = new FakeTimeProvider(now ?? new DateTimeOffset(2026, 7, 30, 9, 0, 0, TimeSpan.Zero));

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            // The container is plain Postgres, not PgBouncer — and the outbox dispatcher's
            // LISTEN/NOTIFY needs a session, which transaction pooling would take away.
            ["Postgres:PgBouncerTransactionMode"] = "false",
            ["ConnectionStrings:Redis"] = redis.ConnectionString,
            ["Kafka:BootstrapServers"] = redpanda.IsAvailable ? redpanda.BootstrapServers : "127.0.0.1:1",
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = tokens.IssuerName,
            ["Jwt:RequireHttpsMetadata"] = "false",
            ["Wallet:InternalApiKey"] = InternalApiKey,
            ["Onepay:WebhookSecret"] = OnepayWebhookSecret,
            ["ComBankIpg:WebhookSecret"] = LankaQrWebhookSecret,
            // AL-15's deep link, as a template. No outbound call is made for LankaQR at all.
            ["LankaQr:DeepLinkTemplate"] = "combank://pay?ref={orderId}&amount={amountMinor}",
            ["LankaQr:MerchantId"] = "MR-TEST-MERCHANT",
            ["urls"] = "http://127.0.0.1:0",
            // One /metrics endpoint per harness would collide across concurrently running tests.
            ["Otel:PrometheusEnabled"] = "false",
            // Off by default: the dispatcher publishing underneath an outbox assertion makes "the row
            // was queued" indistinguishable from "something drained it". The tests that read the topic
            // turn it on.
            ["Outbox:DispatcherEnabled"] = "false",
        };

        if (settings is not null)
        {
            foreach (var (key, value) in settings)
            {
                overrides[key] = value;
            }
        }

        var app = WalletApplication.Build(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = AppContext.BaseDirectory,
            },
            builder =>
            {
                // MAGERIDE_TEST_LOGS=1 keeps the console provider when a failure needs a trace.
                if (Environment.GetEnvironmentVariable("MAGERIDE_TEST_LOGS") != "1")
                {
                    builder.Logging.ClearProviders();
                }

                builder.Configuration.AddInMemoryCollection(overrides);

                // Ahead of AddMageRideDefaults's TryAddSingleton, so every settlement instant, every
                // event timestamp and the 90-second window run on the test's clock.
                builder.Services.AddSingleton<TimeProvider>(clock);

                // PostConfigure so this runs after the kernel's AddMageRideAuth has built the options.
                builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                    .PostConfigure(bearer =>
                    {
                        bearer.ConfigurationManager = null;
                        bearer.TokenValidationParameters.IssuerSigningKey = tokens.PublicKey;
                        bearer.TokenValidationParameters.IssuerSigningKeyResolver = null;
                    });
            });

        await app.StartAsync();

        return new WalletHarness(app, postgres, redis, redpanda, tokens, clock);
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
            request.Headers.TryAddWithoutValidation(InternalWalletEndpoints.ApiKeyHeader, internalKey);
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

    /// <summary>
    /// Posts a provider callback the way a gateway does: an HMAC over the exact bytes sent.
    /// </summary>
    /// <remarks>
    /// The signature is computed over the serialised body and nothing is re-serialised afterwards,
    /// which is the whole point of the rule <c>_shared.yaml</c> states — a round-tripped body has a
    /// different digest.
    /// </remarks>
    public async Task<HttpResponseMessage> PostSignedCallbackAsync(
        string path, object body, string secret, string? signatureOverride = null)
    {
        var json = JsonSerializer.Serialize(body, MageRideJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json);

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(bytes),
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation(
            WebhookSignature.HeaderName,
            signatureOverride ?? WebhookSignature.Compute(bytes, secret));

        return await Client.SendAsync(request);
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
    // Seeding. wallet-svc creates no accounts — iam-svc does — and the tier ladder is 1901's.
    // -----------------------------------------------------------------------------------------

    /// <summary>An <c>iam.users</c> row with the driver role, plus a bearer for it.</summary>
    public async Task<SeededDriver> CreateDriverAsync(long openingBalanceMinor = 0)
    {
        var id = await CreateUserAsync("driver");

        if (openingBalanceMinor != 0)
        {
            await CreditDirectlyAsync(id, openingBalanceMinor);
        }

        return new SeededDriver(id, Tokens.Driver(id));
    }

    public async Task<Guid> CreateUserAsync(string role)
    {
        var id = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, phone, role, first_name) VALUES (@Id, @Phone, @Role, @Name);
            INSERT INTO iam.user_roles (user_id, role) VALUES (@Id, @Role) ON CONFLICT DO NOTHING;
            """,
            new
            {
                Id = id,
                Phone = "+9477" + Random.Shared.NextInt64(1_000_000, 9_999_999).ToString(CultureInfo.InvariantCulture),
                Role = role,
                Name = $"Driver {id.ToString()[..8]}",
            });

        return id;
    }

    /// <summary>
    /// Gives a driver an opening balance the way an admin adjustment would — through the ledger.
    /// </summary>
    /// <remarks>
    /// Deliberately not an <c>UPDATE billing.accounts SET balance_minor</c>: a balance that did not come
    /// from postings is a balance the ledger disagrees with, and a suite that seeded one would be
    /// testing against a state this service can never produce.
    /// </remarks>
    public async Task CreditDirectlyAsync(Guid driverId, long amountMinor)
    {
        using var response = await PostAsync(
            $"/v1/internal/wallet/{driverId}/credit",
            new
            {
                amountMinor,
                kind = "adjustment",
                idempotencyKey = $"test-opening:{driverId}:{Guid.NewGuid()}",
                description = "opening balance",
            },
            internalKey: InternalApiKey);

        var text = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == System.Net.HttpStatusCode.OK,
            $"Seeding a balance for {driverId} returned {(int)response.StatusCode}: {text}");
    }

    // -----------------------------------------------------------------------------------------
    // Asserting against the ledger itself
    // -----------------------------------------------------------------------------------------

    public Task<NpgsqlConnection> OpenAsync() => _postgres.OpenAsync();

    /// <summary>Σ of every posting of one entry. The D-09 invariant, read from the postings.</summary>
    public async Task<long> EntrySumAsync(Guid entryId)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<long>(
            "SELECT coalesce(sum(amount_minor), 0) FROM billing.journal_postings WHERE entry_id = @EntryId;",
            new { EntryId = entryId });
    }

    /// <summary>Σ of every posting in the ledger. Must be zero, always, whatever happened.</summary>
    public async Task<long> LedgerSumAsync()
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<long>(
            "SELECT coalesce(sum(amount_minor), 0) FROM billing.journal_postings;");
    }

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

    /// <summary>The <c>billing.wallets</c> mirror, for the tests that assert the two agree.</summary>
    public async Task<long?> MirrorBalanceAsync(Guid driverId)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<long?>(
            """
            SELECT w.balance_minor
              FROM billing.wallets w
              JOIN billing.accounts a ON a.id = w.account_id
             WHERE a.owner_type = 'driver' AND a.owner_id = @DriverId;
            """,
            new { DriverId = driverId });
    }

    /// <summary>How many entries of one kind exist. Zero fee rows is asserted with this.</summary>
    public async Task<int> EntryCountAsync(string kind)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM billing.journal_entries WHERE kind = @Kind;",
            new { Kind = kind });
    }

    /// <summary>Every posting of one entry, so a test can see exactly how many legs it had.</summary>
    public async Task<IReadOnlyList<(Guid AccountId, long AmountMinor)>> PostingsAsync(Guid entryId)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<(Guid AccountId, long AmountMinor)>(
            "SELECT account_id, amount_minor FROM billing.journal_postings WHERE entry_id = @EntryId ORDER BY id;",
            new { EntryId = entryId });

        return [.. rows];
    }

    /// <summary>Rows of <c>billing.outbox</c> of one event type, oldest first.</summary>
    public async Task<IReadOnlyList<OutboxRowView>> OutboxAsync(string? eventType = null)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<OutboxRowView>(
            """
            SELECT aggregate_id AS AggregateId, event_type AS EventType, payload::text AS Payload
              FROM billing.outbox
             WHERE @EventType::text IS NULL OR event_type = @EventType
             ORDER BY id;
            """,
            new { EventType = eventType });

        return [.. rows];
    }

    /// <summary>The D-08 cache value dispatch-svc would read, or null when the key is absent.</summary>
    /// <remarks>
    /// Read through the service's own multiplexer, which is the same server dispatch-svc's gate connects
    /// to in the dev stack — so this asserts the key exists where the *other* service looks for it.
    /// </remarks>
    public async Task<long?> CachedBalanceAsync(Guid driverId)
    {
        var redis = Services.GetRequiredService<IConnectionMultiplexer>();
        var raw = await redis.GetDatabase().StringGetAsync(RedisKeys.WalletBalance(driverId));

        return raw.HasValue && long.TryParse(raw.ToString(), out var parsed) ? parsed : null;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        try
        {
            await _app.StopAsync(TimeSpan.FromSeconds(10));
            await _app.DisposeAsync();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"warning: could not stop the harness service: {exception.Message}");
        }
    }

    /// <summary>
    /// Empties what this service owns, and nothing else.
    /// </summary>
    /// <remarks>
    /// The TestKit shares one container per collection and does not reset between tests, and every
    /// assertion here is about money — "the ledger sums to zero" is a claim about the *whole* table, so a
    /// posting another test left behind is part of this test's answer. The platform and suspense accounts
    /// survive (migration 1101 seeds them as singletons and every entry needs one), and their balances
    /// are zeroed with the postings that moved them. <c>billing.voucher_discount_tiers</c> is 1901's
    /// reference data and is left alone; the tests that change a tier put it back.
    /// </remarks>
    private static async Task ResetAsync(PostgresFixture postgres)
    {
        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            TRUNCATE billing.journal_postings, billing.wallet_transactions, billing.outbox,
                     billing.command_log, billing.topups, billing.voucher_purchases,
                     billing.credit_transfers;
            DELETE FROM billing.journal_entries;
            DELETE FROM billing.wallets;
            DELETE FROM billing.accounts WHERE owner_type IN ('driver','fleet');
            UPDATE billing.accounts SET balance_minor = 0 WHERE owner_type IN ('platform','suspense');
            """);
    }
}

/// <summary>A seeded driver and a bearer for them.</summary>
internal sealed record SeededDriver(Guid Id, string Bearer);

/// <summary>One <c>billing.outbox</c> row, read back.</summary>
/// <remarks>
/// <c>Payload</c> comes off a <c>jsonb</c> column, which Postgres re-renders on the way out — keys are
/// reordered and a space follows every colon — so a test asserts on <see cref="Json"/> and never on a
/// substring of the raw text. The value is the same JSON either way; the bytes are not.
/// </remarks>
internal sealed record OutboxRowView(Guid AggregateId, string EventType, string Payload)
{
    /// <summary>The payload, parsed.</summary>
    public JsonElement Json => JsonDocument.Parse(Payload).RootElement.Clone();

    /// <summary>A named number from the payload.</summary>
    public long Number(string property) => Json.GetProperty(property).GetInt64();

    /// <summary>A named string from the payload.</summary>
    public string? Text(string property) =>
        Json.TryGetProperty(property, out var value) ? value.GetString() : null;
}
