using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using MageRide.FleetBilling.Billing;
using MageRide.FleetBilling.Endpoints;
using MageRide.Shared.Http;
using MageRide.Shared.Payments;
using MageRide.TestKit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace MageRide.FleetBilling.Tests.Infrastructure;

/// <summary>
/// A running fleet-billing-svc and a running wallet-svc on real sockets, against a real Postgres.
/// </summary>
/// <remarks>
/// <para>
/// Both are built through their own composition roots, so the pipelines under test — the bearer
/// handler, the problem+json handler, the idempotency middleware, the access filter, the two
/// internal-key filters, the resilience pipeline on the ledger seam — are the ones the processes
/// run.
/// </para>
/// <para>
/// <b>wallet-svc is real, not stubbed.</b> The definition of done says an invoice's lines "post to a
/// balanced journal entry", and both halves of that live in another service's schema:
/// <c>trg_balanced</c>, a DEFERRABLE constraint trigger that fires at COMMIT, and the UNIQUE
/// <c>billing.journal_entries.idempotency_key</c> that makes settling twice move the money once. A
/// stub would assert this suite's own arithmetic. Subscription.Api.Tests boots wallet-svc for
/// exactly the same reason.
/// </para>
/// <para>
/// <b>The clock is a <see cref="FakeTimeProvider"/> shared by both services</b>, so a seven-day
/// payment term and a three-day dunning interval are facts a test can state rather than wait for.
/// </para>
/// </remarks>
internal sealed class FleetBillingHarness : IAsyncDisposable
{
    /// <summary>The interim shared secret this service's internal plane demands.</summary>
    public const string InternalApiKey = "c060-fleet-billing-internal-key-not-a-secret";

    /// <summary>The one wallet-svc's internal ledger plane demands.</summary>
    public const string WalletInternalApiKey = "c060-wallet-internal-key-not-a-secret";

    /// <summary>The OnePay callback secret this harness signs with.</summary>
    public const string OnepayWebhookSecret = "c060-onepay-webhook-secret-not-a-secret";

    /// <summary>The LankaQR / ComBank IPG callback secret.</summary>
    public const string LankaQrWebhookSecret = "c060-lankaqr-webhook-secret-not-a-secret";

    /// <summary>
    /// 09:00 UTC on 15 July 2026 — 14:30 in Colombo, mid-month and mid-day, so a test that adds a
    /// few hours crosses neither a business-date nor a month boundary by accident.
    /// </summary>
    public static readonly DateTimeOffset DefaultNow = new(2026, 7, 15, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The Colombo month <see cref="DefaultNow"/> falls in.</summary>
    public static readonly DateOnly DefaultPeriod = new(2026, 7, 1);

    private readonly WebApplication _billing;
    private readonly WebApplication _wallet;
    private readonly PostgresFixture _postgres;

    private FleetBillingHarness(
        WebApplication billing,
        WebApplication wallet,
        PostgresFixture postgres,
        RedpandaFixture redpanda,
        TestTokenIssuer tokens,
        FakeTimeProvider clock)
    {
        _billing = billing;
        _wallet = wallet;
        _postgres = postgres;
        Redpanda = redpanda;
        Tokens = tokens;
        Clock = clock;

        Client = new HttpClient { BaseAddress = new Uri(AddressOf(billing)), Timeout = TimeSpan.FromSeconds(120) };
        Wallet = new HttpClient { BaseAddress = new Uri(AddressOf(wallet)), Timeout = TimeSpan.FromSeconds(120) };
        Seed = new FleetBillingSeed(postgres, this);
    }

    public HttpClient Client { get; }

    /// <summary>wallet-svc directly — for seeding a fleet balance the way an adjustment would.</summary>
    public HttpClient Wallet { get; }

    public TestTokenIssuer Tokens { get; }

    public FakeTimeProvider Clock { get; }

    public RedpandaFixture Redpanda { get; }

    public FleetBillingSeed Seed { get; }

    public IServiceProvider Services => _billing.Services;

    public static async Task<FleetBillingHarness> StartAsync(
        PostgresFixture postgres,
        RedisFixture redis,
        RedpandaFixture redpanda,
        IDictionary<string, string?>? settings = null,
        DateTimeOffset? now = null,
        bool startWallet = true)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(redpanda);

        postgres.RequireAvailable();
        redis.RequireAvailable();
        await postgres.EnsureMigratedAsync();
        await ResetAsync(postgres);

        var tokens = new TestTokenIssuer();
        var clock = new FakeTimeProvider(now ?? DefaultNow);

        var wallet = BuildWallet(postgres, redis, tokens, clock);
        await wallet.StartAsync();

        var walletAddress = AddressOf(wallet);

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            // The container is plain Postgres, not PgBouncer — and the outbox dispatcher's
            // LISTEN/NOTIFY needs a session, which transaction pooling would take away.
            ["Postgres:PgBouncerTransactionMode"] = "false",
            ["Kafka:BootstrapServers"] = redpanda.IsAvailable ? redpanda.BootstrapServers : "127.0.0.1:1",
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = tokens.IssuerName,
            ["Jwt:RequireHttpsMetadata"] = "false",
            ["FleetBilling:InternalApiKey"] = InternalApiKey,
            ["FleetBilling:WalletBaseUrl"] = startWallet ? walletAddress : null,
            ["FleetBilling:WalletInternalApiKey"] = startWallet ? WalletInternalApiKey : null,
            ["Onepay:WebhookSecret"] = OnepayWebhookSecret,
            ["ComBankIpg:WebhookSecret"] = LankaQrWebhookSecret,
            // AL-15's deep link, as a template. No outbound call is made for LankaQR at all, which
            // is why it is the rail these tests open sessions on.
            ["LankaQr:DeepLinkTemplate"] = "combank://pay?ref={orderId}&amount={amountMinor}",
            ["LankaQr:MerchantId"] = "MR-TEST-MERCHANT",
            // Off by default: a background run raising invoices underneath an assertion makes "the
            // route did it" indistinguishable from "the runner did". The runner's own test turns it
            // on, and every other test drives the phases directly.
            ["FleetBilling:InvoicingEnabled"] = "false",
            ["urls"] = "http://127.0.0.1:0",
            // One /metrics endpoint per harness would collide across concurrently running tests.
            ["Otel:PrometheusEnabled"] = "false",
            // Off by default: the dispatcher publishing underneath an outbox assertion makes "the
            // row was queued" indistinguishable from "something drained it". The test that reads
            // the topic turns it on.
            ["Outbox:DispatcherEnabled"] = "false",
        };

        if (settings is not null)
        {
            foreach (var (key, value) in settings)
            {
                overrides[key] = value;
            }
        }

        var billing = FleetBillingApplication.Build(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = AppContext.BaseDirectory,
            },
            builder =>
            {
                Quieten(builder);
                builder.Configuration.AddInMemoryCollection(overrides);

                // Ahead of AddMageRideDefaults's TryAddSingleton, so every due date, every
                // settlement instant and every dunning cutoff run on the test's clock.
                builder.Services.AddSingleton<TimeProvider>(clock);
                UseTestSigningKey(builder, tokens);
            });

        await billing.StartAsync();

        return new FleetBillingHarness(billing, wallet, postgres, redpanda, tokens, clock);
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

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path} returned {(int)response.StatusCode}: {text}");

        return JsonSerializer.Deserialize<T>(text, MageRideJson.Options)!;
    }

    /// <summary>The bytes of a download, plus the content type the route chose.</summary>
    public async Task<(byte[] Bytes, string? ContentType)> DownloadAsync(string path, string bearer)
    {
        using var response = await GetAsync(path, bearer);
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"GET {path} returned {(int)response.StatusCode}: {Encoding.UTF8.GetString(bytes)}");

        return (bytes, response.Content.Headers.ContentType?.MediaType);
    }

    public Task<HttpResponseMessage> PostAsync(
        string path, object? body = null, string? bearer = null, string? internalKey = null) =>
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
            request.Headers.TryAddWithoutValidation(InternalFleetBillingEndpoints.ApiKeyHeader, internalKey);
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

        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(bytes) };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation(
            WebhookSignature.HeaderName, signatureOverride ?? WebhookSignature.Compute(bytes, secret));

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
    // Driving the phases directly, without a timer
    // -----------------------------------------------------------------------------------------

    public async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        using var scope = _billing.Services.CreateScope();

        return await action(scope.ServiceProvider);
    }

    public Task<MageRide.FleetBilling.Domain.InvoiceRunResult> GenerateAsync(DateOnly? period = null) =>
        WithScopeAsync(services =>
        {
            var generation = services.GetRequiredService<IInvoiceRunService>();

            return generation.RunAsync(period ?? generation.CurrentPeriod(), CancellationToken.None);
        });

    public Task<MageRide.FleetBilling.Domain.SettlementRunResult> SettleAsync(Guid? fleetId = null) =>
        WithScopeAsync(services =>
            services.GetRequiredService<IInvoiceSettlementService>().RunAsync(fleetId, CancellationToken.None));

    public Task<MageRide.FleetBilling.Domain.DunningRunResult> DunAsync() =>
        WithScopeAsync(services =>
            services.GetRequiredService<IDunningService>().RunAsync(CancellationToken.None));

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

    /// <summary>A fleet's balance, from <c>billing.accounts</c> — the master (§10).</summary>
    public async Task<long> BalanceAsync(Guid fleetId)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<long?>(
            """
            SELECT balance_minor FROM billing.accounts
             WHERE owner_type = 'fleet' AND owner_id = @FleetId AND currency = 'LKR';
            """,
            new { FleetId = fleetId }) ?? 0;
    }

    /// <summary>How many entries of one kind exist. "No fee row was ever written" is asserted with this.</summary>
    public async Task<int> EntryCountAsync(string kind)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM billing.journal_entries WHERE kind = @Kind;", new { Kind = kind });
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

    /// <summary>Rows of <c>billing.fleet_outbox</c>, oldest first.</summary>
    public async Task<IReadOnlyList<OutboxRowView>> OutboxAsync(string? eventType = null)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<OutboxRowView>(
            """
            SELECT aggregate_id AS AggregateId, event_type AS EventType, payload::text AS Payload
              FROM billing.fleet_outbox
             WHERE @EventType::text IS NULL OR event_type = @EventType
             ORDER BY id;
            """,
            new { EventType = eventType });

        return [.. rows];
    }

    /// <summary>The status of one <c>billing.monthly_subscriptions</c> row, by vehicle and month.</summary>
    public async Task<string?> ChargeStatusAsync(Guid vehicleId, DateOnly periodMonth)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<string?>(
            """
            SELECT status FROM billing.monthly_subscriptions
             WHERE vehicle_id = @VehicleId AND period_month = @PeriodMonth;
            """,
            new { VehicleId = vehicleId, PeriodMonth = periodMonth });
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        Wallet.Dispose();

        await StopAsync(_billing);
        await StopAsync(_wallet);
    }

    // -----------------------------------------------------------------------------------------
    // Composition
    // -----------------------------------------------------------------------------------------

    /// <remarks>
    /// Kafka is pointed at a dead address and the outbox dispatcher is off. This suite asserts on
    /// the ledger, not on <c>wallet.events</c>; what wallet-svc publishes is C046's suite.
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

                builder.Configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
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
    /// Empties what this component and its fixtures own, and nothing else.
    /// </summary>
    /// <remarks>
    /// The TestKit shares one container per collection and does not reset between tests, and every
    /// assertion here is about money — "the ledger sums to zero" is a claim about the *whole* table,
    /// so a posting another test left behind is part of this test's answer. The platform and
    /// suspense accounts survive (migration 1101 seeds them as singletons and every entry needs
    /// one) and their balances are zeroed with the postings that moved them.
    /// </remarks>
    private static async Task ResetAsync(PostgresFixture postgres)
    {
        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            TRUNCATE billing.fleet_invoice_lines, billing.fleet_outbox, billing.fleet_command_log,
                     billing.fleet_topups, billing.journal_postings, billing.wallet_transactions,
                     billing.outbox, billing.command_log, billing.topups;
            DELETE FROM billing.fleet_invoices;
            DELETE FROM billing.monthly_subscriptions;
            DELETE FROM billing.journal_entries;
            DELETE FROM billing.wallets;
            DELETE FROM billing.accounts WHERE owner_type IN ('driver','fleet');
            UPDATE billing.accounts SET balance_minor = 0 WHERE owner_type IN ('platform','suspense');
            DELETE FROM registry.fleet_vehicles;
            DELETE FROM registry.vehicles WHERE registration_number LIKE 'C060%';
            DELETE FROM iam.fleet_members;
            DELETE FROM registry.fleets;
            DELETE FROM iam.users WHERE first_name LIKE 'C060%';
            """);
    }
}

/// <summary>One <c>billing.fleet_outbox</c> row, read back.</summary>
/// <remarks>
/// <c>Payload</c> comes off a <c>jsonb</c> column, which Postgres re-renders on the way out — keys
/// are reordered and a space follows every colon — so a test asserts on <see cref="Json"/> and never
/// on a substring of the raw text.
/// </remarks>
internal sealed record OutboxRowView(Guid AggregateId, string EventType, string Payload)
{
    public JsonElement Json => JsonDocument.Parse(Payload).RootElement.Clone();

    public long Number(string property) => Json.GetProperty(property).GetInt64();

    public string? Text(string property) =>
        Json.TryGetProperty(property, out var value) ? value.GetString() : null;
}

/// <summary>An organisation and the Owner who runs it.</summary>
internal sealed record SeededFleet(Guid Id, Guid OwnerId, string Bearer, string Name);

/// <summary>A vehicle on a fleet's roster.</summary>
internal sealed record SeededVehicle(Guid Id, string RegistrationNumber, string Mode);

/// <summary>
/// Seeds the rows other components own: the organisation, its roster, and the per-vehicle charges
/// subscription-svc's runner raises.
/// </summary>
/// <remarks>
/// <b>The Mode B charges are raised with subscription-svc's own statement, not with an INSERT of
/// this suite's invention.</b> "Mode A vehicles never appear as a charged line" is only a real
/// assertion if the rows this component consolidates were produced the way production produces
/// them — a hand-written INSERT that happened to skip Mode A would be the suite agreeing with
/// itself. <see cref="RaiseModeBChargesAsync"/> is `ModeBBillingRepository.RaiseMonthAsync`'s SQL,
/// transcribed; <c>The_raise_this_suite_seeds_with_produces_no_Mode_A_charge</c> asserts the
/// transcription still holds the fence, which is the property that matters rather than the
/// characters.
/// </remarks>
internal sealed class FleetBillingSeed(PostgresFixture postgres, FleetBillingHarness harness)
{
    /// <summary>
    /// subscription-svc's raise query (C047 `ModeBBillingRepository`), transcribed.
    /// </summary>
    /// <remarks>
    /// The <c>WHERE v.mode = 'B'</c> is the fence: a Mode A vehicle never gets a charge row, so it
    /// can never become an invoice line. Kept as a constant so the test that compares it with the
    /// production string has something to compare.
    /// </remarks>
    public const string ModeBRaiseSql =
        """
        INSERT INTO billing.monthly_subscriptions
          (vehicle_id, period_month, period_month_tz_at, amount_minor, status)
        SELECT v.id,
               @PeriodMonth,
               @Now,
               CASE WHEN date_trunc('month', v.created_at AT TIME ZONE 'Asia/Colombo')::date = @PeriodMonth
                    THEN 0 ELSE @FeeMinor END,
               CASE WHEN date_trunc('month', v.created_at AT TIME ZONE 'Asia/Colombo')::date = @PeriodMonth
                    THEN 'FREE' ELSE 'DUE' END
          FROM registry.vehicles v
         WHERE v.mode = 'B'
           AND v.status = 'APPROVED'
           AND date_trunc('month', v.created_at AT TIME ZONE 'Asia/Colombo')::date <= @PeriodMonth
        ON CONFLICT (vehicle_id, period_month) DO NOTHING;
        """;

    private int _plate;

    /// <summary>An APPROVED organisation, its Owner, and a bearer for them.</summary>
    public async Task<SeededFleet> CreateFleetAsync(string status = "APPROVED", string? name = null)
    {
        var ownerId = await CreateUserAsync("fleet_owner");
        var fleetId = Guid.NewGuid();
        var fleetName = name ?? $"C060 Transport {fleetId.ToString()[..8]}";

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO registry.fleets (id, owner_id, name, business_reg, status)
            VALUES (@Id, @OwnerId, @Name, @BusinessReg, @Status);
            INSERT INTO iam.fleet_members (fleet_id, user_id, fleet_role)
            VALUES (@Id, @OwnerId, 'owner') ON CONFLICT DO NOTHING;
            """,
            new
            {
                Id = fleetId,
                OwnerId = ownerId,
                Name = fleetName,
                BusinessReg = $"PV-{fleetId.ToString()[..8]}",
                Status = status,
            });

        return new SeededFleet(fleetId, ownerId, harness.Tokens.FleetUser(ownerId, fleetId, "owner"), fleetName);
    }

    /// <summary>Adds a member with a sub-role, and returns a bearer for them.</summary>
    public async Task<(Guid UserId, string Bearer)> AddMemberAsync(Guid fleetId, string fleetRole)
    {
        var userId = await CreateUserAsync("fleet_owner");

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            "INSERT INTO iam.fleet_members (fleet_id, user_id, fleet_role) VALUES (@FleetId, @UserId, @Role);",
            new { FleetId = fleetId, UserId = userId, Role = fleetRole });

        // The claim says `owner` whatever the row says — that combination is what the access filter
        // exists to refuse (C027 puts the caller's most privileged membership in the token).
        return (userId, harness.Tokens.FleetUser(userId, fleetId, "owner"));
    }

    /// <summary>An APPROVED vehicle on a fleet's roster.</summary>
    /// <param name="mode">`A` (free, AL-03) or `B` (the charged one). Never `C` — the CHECK refuses it.</param>
    /// <param name="createdAt">
    /// Drives the first-free-month rule: a vehicle whose Colombo creation month is the period is
    /// billed zero (D5' §2.1, §20).
    /// </param>
    public async Task<SeededVehicle> AddVehicleAsync(
        SeededFleet fleet, string mode = "B", DateTimeOffset? createdAt = null, string vehicleType = "van")
    {
        ArgumentNullException.ThrowIfNull(fleet);

        var vehicleId = Guid.NewGuid();
        var plate = $"C060-{Interlocked.Increment(ref _plate):D4}";

        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO registry.vehicles
                (id, owner_id, registration_number, vehicle_type, mode, status, driver_name, created_at)
            VALUES (@Id, @OwnerId, @Plate, @VehicleType, @Mode, 'APPROVED', 'C060 driver', @CreatedAt);
            INSERT INTO registry.fleet_vehicles (fleet_id, vehicle_id, mode)
            VALUES (@FleetId, @Id, @Mode);
            """,
            new
            {
                Id = vehicleId,
                fleet.OwnerId,
                Plate = plate,
                VehicleType = vehicleType,
                Mode = mode,
                FleetId = fleet.Id,
                // A month before the harness's clock by default, so the vehicle is out of its free
                // month and is actually charged.
                CreatedAt = createdAt ?? FleetBillingHarness.DefaultNow.AddMonths(-3),
            });

        return new SeededVehicle(vehicleId, plate, mode);
    }

    /// <summary>Runs subscription-svc's monthly raise for one Colombo month.</summary>
    public async Task<int> RaiseModeBChargesAsync(DateOnly? periodMonth = null, long feeMinor = 30_000)
    {
        await using var connection = await postgres.OpenAsync();

        return await connection.ExecuteAsync(
            ModeBRaiseSql,
            new
            {
                PeriodMonth = periodMonth ?? FleetBillingHarness.DefaultPeriod,
                Now = harness.Clock.GetUtcNow(),
                FeeMinor = feeMinor,
            });
    }

    /// <summary>
    /// Gives a fleet an opening balance the way an admin adjustment would — through the ledger.
    /// </summary>
    /// <remarks>
    /// Deliberately not an <c>UPDATE billing.accounts SET balance_minor</c>: a balance that did not
    /// come from postings is a balance the ledger disagrees with, and a suite that seeded one would
    /// be testing against a state this platform can never produce.
    /// </remarks>
    public async Task CreditAsync(Guid fleetId, long amountMinor, string? key = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/internal/wallet/fleet/{fleetId}/credit")
        {
            Content = JsonContent.Create(
                new
                {
                    amountMinor,
                    kind = "adjustment",
                    idempotencyKey = key ?? $"c060-opening:{fleetId}:{Guid.NewGuid()}",
                    description = "opening balance",
                },
                options: MageRideJson.Options),
        };

        request.Headers.TryAddWithoutValidation(
            "X-MageRide-Internal-Key", FleetBillingHarness.WalletInternalApiKey);

        using var response = await harness.Wallet.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Seeding a balance for fleet {fleetId} returned {(int)response.StatusCode}: {text}");
    }

    private async Task<Guid> CreateUserAsync(string role)
    {
        var id = Guid.NewGuid();

        await using var connection = await postgres.OpenAsync();

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
                Name = $"C060 {id.ToString()[..8]}",
            });

        return id;
    }
}
