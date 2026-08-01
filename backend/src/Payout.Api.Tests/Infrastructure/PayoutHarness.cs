using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using MageRide.Shared.Http;
using MageRide.TestKit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace MageRide.Payout.Tests.Infrastructure;

/// <summary>
/// A running payout-svc on a real socket, beside a **real wallet-svc**, over a real Postgres.
/// </summary>
/// <remarks>
/// <para>
/// Built through <see cref="PayoutApplication.Build"/> so the pipeline under test — the bearer
/// handler, the URD §2.3 authorization handler, the problem+json handler and the start-up
/// announcements — is the one the process runs.
/// </para>
/// <para>
/// <b>wallet-svc is booted, not stubbed.</b> Half of what AL-58 guarantees lives on its side of the
/// seam: the debit itself, `billing.journal_entries.idempotency_key` UNIQUE, and the balance a
/// sweep is asserted against. A stub would be this suite asserting against its own fixture.
/// </para>
/// <para>
/// <b>The bank is stubbed, because there is no bank.</b> ADD §1.18 leaves the provider unchosen, so
/// <see cref="Bank"/> is a recording socket a test points the service at — and the default is to
/// point it nowhere, which is the deployed state and the one the DoD asks about.
/// </para>
/// </remarks>
internal sealed class PayoutHarness : IAsyncDisposable
{
    public const string WalletInternalApiKey = "c133-wallet-internal-key-not-a-secret";
    public const string InternalApiKey = "c133-payout-internal-key-not-a-secret";

    /// <summary>Hands each harness its own Colombo run date — see <c>StartAsync</c>.</summary>
    private static int _harnessCount;

    private readonly WebApplication _app;
    private readonly WebApplication _wallet;
    private readonly PostgresFixture _postgres;

    private PayoutHarness(
        WebApplication app,
        WebApplication wallet,
        PostgresFixture postgres,
        TestTokenIssuer tokens,
        FakeTimeProvider clock,
        StubBank? bank)
    {
        _app = app;
        _wallet = wallet;
        _postgres = postgres;
        Tokens = tokens;
        Clock = clock;
        Bank = bank;

        Client = new HttpClient { BaseAddress = new Uri(AddressOf(app)), Timeout = TimeSpan.FromSeconds(120) };
        Wallet = new HttpClient { BaseAddress = new Uri(AddressOf(wallet)), Timeout = TimeSpan.FromSeconds(120) };
    }

    public HttpClient Client { get; }

    public HttpClient Wallet { get; }

    public TestTokenIssuer Tokens { get; }

    public FakeTimeProvider Clock { get; }

    /// <summary>The bank stub, when the test asked for one. Null is the deployed state.</summary>
    public StubBank? Bank { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<PayoutHarness> StartAsync(
        PostgresFixture postgres,
        RedisFixture redis,
        StubBank? bank = null,
        IDictionary<string, string?>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        postgres.RequireAvailable();
        await postgres.EnsureMigratedAsync();

        var tokens = new TestTokenIssuer();

        // A Sunday, so the runner's own day check is satisfied where a test drives it — and **a
        // different Sunday for every harness**, because `billing.payout_batches.run_date` is UNIQUE
        // across the whole database and this collection shares one Postgres. Without that, the
        // first test to sweep would open the batch for the shared date and every later test would
        // be told the day was already done.
        //
        // Four weeks apart rather than one: a test that advances its own clock by seven days to
        // reach "next week's run" must not land on the next harness's date.
        var week = Interlocked.Increment(ref _harnessCount);
        var clock = new FakeTimeProvider(
            new DateTimeOffset(2026, 8, 2, 3, 0, 0, TimeSpan.Zero).AddDays(28 * week));

        var wallet = BuildWallet(postgres, redis, tokens, clock);
        await wallet.StartAsync();

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            ["Postgres:PgBouncerTransactionMode"] = "false",
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = tokens.IssuerName,
            ["Jwt:RequireHttpsMetadata"] = "false",

            ["Payout:WalletBaseUrl"] = AddressOf(wallet),
            ["Payout:WalletInternalApiKey"] = WalletInternalApiKey,
            ["Payout:InternalApiKey"] = InternalApiKey,
            ["Payout:BankBaseUrl"] = bank?.BaseUrl,

            // Off unless a test drives the sweep directly: a background pass landing under an
            // assertion makes "the run swept it" indistinguishable from "a previous tick did".
            ["Payout:Enabled"] = "false",

            ["urls"] = "http://127.0.0.1:0",
            ["Otel:PrometheusEnabled"] = "false",
        };

        if (settings is not null)
        {
            foreach (var (key, value) in settings)
            {
                overrides[key] = value;
            }
        }

        var app = PayoutApplication.Build(
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

        await app.StartAsync();

        return new PayoutHarness(app, wallet, postgres, tokens, clock, bank);
    }

    // -----------------------------------------------------------------------------------------
    // Seeding — written straight to Postgres
    // -----------------------------------------------------------------------------------------

    /// <summary>A driver with a wallet balance, and optionally a verified payout profile.</summary>
    /// <remarks>
    /// The balance is credited through wallet-svc's own internal route rather than by SQL, so the
    /// `billing.wallets` mirror and the account row are in the state a real credit leaves them in.
    /// The profile is written directly: it is registry-svc's table and standing that service up
    /// would make this suite a test of C028.
    /// </remarks>
    public async Task<Guid> DriverAsync(long balanceMinor, bool verifiedProfile = true, string? accountNo = null)
    {
        var driverId = Guid.NewGuid();

        await using (var connection = await _postgres.OpenAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO iam.users (id, phone, role, first_name)
                VALUES (@Id, @Phone, 'driver', 'Payout Driver');
                INSERT INTO iam.user_roles (user_id, role) VALUES (@Id, 'driver') ON CONFLICT DO NOTHING;
                """,
                new { Id = driverId, Phone = "+9478" + Random.Shared.NextInt64(1_000_000, 9_999_999) });

            if (verifiedProfile)
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO registry.driver_payout_profiles
                        (driver_id, bank, branch, account_no, account_holder_name, status)
                    VALUES (@Id, 'Bank of Ceylon', 'Kollupitiya', @AccountNo, 'Payout Driver', 'verified');
                    """,
                    new { Id = driverId, AccountNo = accountNo ?? "0071234567" });
            }
        }

        if (balanceMinor > 0)
        {
            await CreditAsync(driverId, balanceMinor);
        }

        return driverId;
    }

    /// <summary>Puts an opening balance on a driver's wallet through wallet-svc's own seam.</summary>
    public async Task CreditAsync(Guid driverId, long amountMinor)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/internal/wallet/{driverId}/credit")
        {
            Content = JsonContent.Create(
                new
                {
                    amountMinor,
                    kind = "adjustment",
                    idempotencyKey = $"test-opening:{driverId}:{Guid.NewGuid()}",
                    description = "opening balance",
                },
                options: MageRideJson.Options),
        };

        request.Headers.TryAddWithoutValidation("X-MageRide-Internal-Key", WalletInternalApiKey);

        using var response = await Wallet.SendAsync(request);

        Assert.True(
            response.IsSuccessStatusCode,
            $"Seeding a balance for {driverId} returned {(int)response.StatusCode}: "
            + await response.Content.ReadAsStringAsync());
    }

    // -----------------------------------------------------------------------------------------
    // Asserting against the database
    // -----------------------------------------------------------------------------------------

    public async Task<long> BalanceAsync(Guid driverId)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<long?>(
            """
            SELECT balance_minor FROM billing.accounts
             WHERE owner_type = 'driver' AND owner_id = @Id AND currency = 'LKR';
            """,
            new { Id = driverId }) ?? 0;
    }

    /// <summary>Every instruction raised for one driver, newest first.</summary>
    public async Task<IReadOnlyList<(Guid Id, long AmountMinor, string Status, string? Reference)>> PayoutsAsync(
        Guid driverId)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<(Guid Id, long AmountMinor, string Status, string? Reference)>(
            """
            SELECT id, amount_minor, status, provider_reference
              FROM billing.payouts WHERE driver_id = @Id ORDER BY created_at DESC;
            """,
            new { Id = driverId });

        return [.. rows];
    }

    /// <summary>Σ over the whole ledger — zero, always (D-09).</summary>
    public async Task<long> LedgerSumAsync()
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<long?>(
            "SELECT coalesce(sum(amount_minor), 0) FROM billing.journal_postings;") ?? 0;
    }

    /// <summary>
    /// How many entries of a kind touched <em>one driver's</em> wallet.
    /// </summary>
    /// <remarks>
    /// Scoped to the driver, not counted platform-wide: this collection shares one Postgres, so a
    /// global count would make every test's answer depend on which others had run.
    /// </remarks>
    public async Task<int> EntryCountAsync(Guid driverId, string kind)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<int>(
            """
            SELECT count(DISTINCT e.id)::int
              FROM billing.journal_entries e
              JOIN billing.journal_postings p ON p.entry_id = e.id
              JOIN billing.accounts a ON a.id = p.account_id
             WHERE e.kind = @Kind AND a.owner_type = 'driver' AND a.owner_id = @DriverId;
            """,
            new { DriverId = driverId, Kind = kind });
    }

    public Task<NpgsqlConnection> OpenAsync() => _postgres.OpenAsync();

    // -----------------------------------------------------------------------------------------
    // Requests
    // -----------------------------------------------------------------------------------------

    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string? bearer = null, object? body = null, string? internalKey = null)
    {
        using var request = new HttpRequestMessage(method, path);

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (internalKey is not null)
        {
            request.Headers.TryAddWithoutValidation("X-MageRide-Internal-Key", internalKey);
        }

        request.Headers.TryAddWithoutValidation(MageRideHeaders.IdempotencyKey, Guid.NewGuid().ToString());

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: MageRideJson.Options);
        }

        return await Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> GetAsync(string path, string bearer) =>
        SendAsync(HttpMethod.Get, path, bearer);

    public async Task<T> OkAsync<T>(HttpResponseMessage response, string what)
    {
        ArgumentNullException.ThrowIfNull(response);

        Assert.True(
            response.IsSuccessStatusCode,
            $"{what} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<T>(MageRideJson.Options))!;
    }

    public static async Task<(string Code, JsonElement Body)> ProblemAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var type = document.RootElement.GetProperty("type").GetString() ?? string.Empty;

        return (type[(type.LastIndexOf('/') + 1)..], document.RootElement.Clone());
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        Wallet.Dispose();

        await _app.StopAsync();
        await _app.DisposeAsync();
        await _wallet.StopAsync();
        await _wallet.DisposeAsync();
    }

    private static string AddressOf(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

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
        if (Environment.GetEnvironmentVariable("MAGERIDE_TEST_LOGS") != "1")
        {
            builder.Logging.ClearProviders();
        }
    }

    private static void UseTestSigningKey(WebApplicationBuilder builder, TestTokenIssuer tokens) =>
        builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .PostConfigure(bearer =>
            {
                bearer.ConfigurationManager = null;
                bearer.TokenValidationParameters.IssuerSigningKey = tokens.PublicKey;
                bearer.TokenValidationParameters.IssuerSigningKeyResolver = null;
            });
}
