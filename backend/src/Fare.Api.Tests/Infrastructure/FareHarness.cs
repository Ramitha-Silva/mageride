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

namespace MageRide.Fare.Tests.Infrastructure;

/// <summary>
/// One Postgres shared by every integration test in this assembly.
/// </summary>
/// <remarks>
/// <b>Postgres carries the invariants, not just the rows.</b> "A rate published today cannot
/// re-price yesterday's ride" is <c>ux_tariffs_type_effective</c> and an <c>ORDER BY effective_from
/// DESC LIMIT 1</c>; "a ride priced twice produces one payment" is a <c>FOR UPDATE</c> inside one
/// transaction. Both are claims about the server.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class FareCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "mageride-fare";
}

/// <summary>A running fare-svc on a real socket against a real Postgres.</summary>
internal sealed class FareHarness : IAsyncDisposable
{
    /// <summary>The interim shared secret fare-svc's internal plane demands.</summary>
    public const string InternalApiKey = "c049-fare-internal-key-not-a-secret";

    /// <summary>
    /// The <c>fareEstimateToken</c> signing key. ride-svc must be configured with the same value or
    /// every booking is a <c>400 invalid-fare-token</c>.
    /// </summary>
    public const string EstimateTokenKey = "c049-fare-estimate-token-key-not-a-secret";

    /// <summary>The HMAC secrets the two payment callbacks are signed with (Δ C050).</summary>


    /// <summary>
    /// 09:00 UTC on 30 July 2026 — 14:30 in Colombo. Deliberately outside every seeded window, so a
    /// test that wants a surcharge has to ask for one.
    /// </summary>
    public static readonly DateTimeOffset DefaultNow = new(2026, 7, 30, 9, 0, 0, TimeSpan.Zero);

    private readonly WebApplication _app;
    private readonly PostgresFixture _postgres;

    private FareHarness(WebApplication app, PostgresFixture postgres, TestTokenIssuer tokens, FakeTimeProvider clock)
    {
        _app = app;
        _postgres = postgres;
        Tokens = tokens;
        Clock = clock;

        Client = new HttpClient { BaseAddress = new Uri(AddressOf(app)), Timeout = TimeSpan.FromSeconds(120) };
        Seed = new FareSeed(postgres);
    }

    public HttpClient Client { get; }

    public TestTokenIssuer Tokens { get; }

    public FakeTimeProvider Clock { get; }

    public FareSeed Seed { get; }

    /// <summary>The composed host's container, for a seam with no HTTP surface (C119's gauges).</summary>
    public IServiceProvider Services => _app.Services;

    public static async Task<FareHarness> StartAsync(
        PostgresFixture postgres,
        IDictionary<string, string?>? settings = null,
        DateTimeOffset? now = null,
        DownstreamStub? downstream = null)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        postgres.RequireAvailable();
        await postgres.EnsureMigratedAsync();
        await ResetAsync(postgres);

        var tokens = new TestTokenIssuer();
        var clock = new FakeTimeProvider(now ?? DefaultNow);

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            ["Postgres:PgBouncerTransactionMode"] = "false",
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = tokens.IssuerName,
            ["Jwt:RequireHttpsMetadata"] = "false",
            ["Fare:EstimateTokenKey"] = EstimateTokenKey,
            ["Fare:InternalApiKey"] = InternalApiKey,
            // The D-05 seam is off unless a test turns it on: a settlement calling a dispatch-svc
            // that is not there would make every calculation log an error, and the tests that are
            // about it stand their own stub up.
            ["Fare:PenaltySettlementEnabled"] = "false",
            // Δ C050. The nudge sweep is off unless a test drives it directly: a background pass
            // logging under an assertion makes "the sweep found it" indistinguishable from "a
            // previous pass did".
            ["Fare:QrNudgeEnabled"] = "false",
            ["Fare:RideBaseUrl"] = downstream?.BaseAddress,
            ["Fare:RideInternalApiKey"] = downstream is null ? null : DownstreamStub.InternalApiKey,
            ["Fare:WalletBaseUrl"] = downstream?.BaseAddress,
            ["Fare:WalletInternalApiKey"] = downstream is null ? null : DownstreamStub.InternalApiKey,
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

        var app = FareApplication.Build(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = AppContext.BaseDirectory,
            },
            builder =>
            {
                if (Environment.GetEnvironmentVariable("MAGERIDE_TEST_LOGS") != "1")
                {
                    builder.Logging.ClearProviders();
                }

                builder.Configuration.AddInMemoryCollection(overrides);
                builder.Services.AddSingleton<TimeProvider>(clock);

                // PostConfigure, so this runs after the kernel's AddMageRideAuth has built the options.
                builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                    .PostConfigure(bearer =>
                    {
                        bearer.ConfigurationManager = null;
                        bearer.TokenValidationParameters.IssuerSigningKey = tokens.PublicKey;
                        bearer.TokenValidationParameters.IssuerSigningKeyResolver = null;
                    });
            });

        await app.StartAsync();

        return new FareHarness(app, postgres, tokens, clock);
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

    public async Task<T> GetAsync<T>(string path, string bearer)
    {
        using var response = await GetAsync(path, bearer);
        var text = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == System.Net.HttpStatusCode.OK,
            $"GET {path} returned {(int)response.StatusCode}: {text}");

        return JsonSerializer.Deserialize<T>(text, MageRideJson.Options)!;
    }

    /// <summary>Calls the internal plane the way ride-svc does.</summary>
    public Task<HttpResponseMessage> CalculateAsync(Guid rideId, double? distanceKm = null) =>
        CalculateAtAsync("/v1/fare/calculate", rideId, distanceKm);

    /// <summary>
    /// The same call against an arbitrary path — so a test can compare the guarded route against one
    /// that was never mapped at all.
    /// </summary>
    public async Task<HttpResponseMessage> CalculateAtAsync(string path, Guid rideId, double? distanceKm = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(
                new { rideId = rideId.ToString(), distanceKm }, options: MageRideJson.Options),
        };

        request.Headers.TryAddWithoutValidation("X-MageRide-Internal-Key", InternalApiKey);
        request.Headers.TryAddWithoutValidation(MageRideHeaders.IdempotencyKey, Guid.NewGuid().ToString());

        return await Client.SendAsync(request);
    }

    /// <summary>A bearer POST with an idempotency key, as the apps send.</summary>
    public async Task<HttpResponseMessage> PostAsync(string path, object? body, string bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.TryAddWithoutValidation(MageRideHeaders.IdempotencyKey, Guid.NewGuid().ToString());

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: MageRideJson.Options);
        }

        return await Client.SendAsync(request);
    }

    /// <summary>A provider callback, signed the way the gateway would (HMAC-SHA256 over the raw body).</summary>
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

    /// <summary>A driver's daily earnings rollup — R-05's record that a trip was earned.</summary>
    public async Task<(int Trips, long GrossMinor)?> EarningsAsync(Guid driverId)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<(int Trips, long GrossMinor)>(
            """
            SELECT trips, gross_minor::bigint AS gross_minor
              FROM fares.driver_earnings WHERE driver_id = @DriverId;
            """,
            new { DriverId = driverId });

        return rows.Cast<(int Trips, long GrossMinor)?>().FirstOrDefault();
    }

    /// <summary>The Finance refund queue as `ix_refunds_open` defines it (SCR-AP-009).</summary>
    public async Task<IReadOnlyList<(Guid Id, string Kind, long AmountMinor, string Status)>> RefundQueueAsync()
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<(Guid Id, string Kind, long AmountMinor, string Status)>(
            """
            SELECT id, kind, amount_minor::bigint AS amount_minor, status
              FROM fares.refunds
             WHERE status IN ('Requested','Submitted')
             ORDER BY requested_at;
            """);

        return [.. rows];
    }

    /// <summary>Support tickets by category — the AL-47 dispute lands here.</summary>
    public async Task<IReadOnlyList<(Guid Id, string Category, string Description)>> TicketsAsync(string category)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<(Guid Id, string Category, string Description)>(
            "SELECT id, category, description FROM support.tickets WHERE category = @Category;",
            new { Category = category });

        return [.. rows];
    }

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
    // Asserting against the rows
    // -----------------------------------------------------------------------------------------

    public Task<NpgsqlConnection> OpenAsync() => _postgres.OpenAsync();

    /// <summary>Every payment row on a ride, oldest first — "priced once" is asserted with this.</summary>
    public async Task<IReadOnlyList<(Guid Id, string State, long AmountMinor, string Method, string PayerRole)>>
        PaymentsAsync(Guid rideId)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<(Guid Id, string State, long AmountMinor, string Method, string PayerRole)>(
            """
            SELECT id, state, amount_minor::bigint AS amount_minor, method, payer_role
              FROM fares.ride_payments
             WHERE ride_id = @RideId
             ORDER BY created_at, attempt_no;
            """,
            new { RideId = rideId });

        return [.. rows];
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
            Console.Error.WriteLine($"warning: could not stop the fare harness: {exception.Message}");
        }
    }

    private static string AddressOf(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

    /// <summary>
    /// Empties what this service owns and restores the reference data 1901 seeds.
    /// </summary>
    /// <remarks>
    /// <b><c>fares.tariffs</c> and <c>fares.peak_windows</c> are restored rather than truncated.</b>
    /// They are 1901's reference data, this component's tests publish new versions over them, and a
    /// suite that left an edited rate card behind would make the next test's price depend on the
    /// order the runner picked.
    /// </remarks>
    private static async Task ResetAsync(PostgresFixture postgres)
    {
        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            TRUNCATE fares.ride_payments, fares.refunds, fares.driver_earnings, fares.command_log CASCADE;
            -- `registry.driver_payouts` was here until migration 1010 dropped it: AL-57/AL-59
            -- retired D-11's OnePay merchant binding, no ride fare reaches an acquirer any more,
            -- and the harness kept truncating a table that no longer exists — which made every
            -- test in this project fail at start-up with `42P01`. Found by C119.
            TRUNCATE support.tickets CASCADE;
            TRUNCATE rides.rides, registry.vehicles CASCADE;
            DELETE FROM telemetry.positions;

            DELETE FROM fares.tariffs;
            INSERT INTO fares.tariffs
              (vehicle_type, first_km_minor, per_km_minor, peak_surcharge_pct, night_surcharge_pct, effective_from)
            VALUES
              ('motorbike',     8000,  6000, 20, 15, 'epoch'::timestamptz),
              ('three_wheeler',10000,  8000, 20, 15, 'epoch'::timestamptz),
              ('flex',         13000,  9000, 20, 15, 'epoch'::timestamptz),
              ('sedan',        15000, 10000, 20, 15, 'epoch'::timestamptz),
              ('mini_van',     15000, 11000, 20, 15, 'epoch'::timestamptz),
              ('van',          15000, 12000, 20, 15, 'epoch'::timestamptz);

            DELETE FROM fares.peak_windows;
            INSERT INTO fares.peak_windows (kind, start_local, end_local, multiplier_pct) VALUES
              ('peak',  '07:00', '09:00', 20),
              ('peak',  '17:00', '19:00', 20),
              ('night', '22:00', '05:00', 15);
            """);
    }
}
