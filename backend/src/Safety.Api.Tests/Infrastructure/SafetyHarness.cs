using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using MageRide.Notification;
using MageRide.Safety.Endpoints;
using MageRide.Safety.Persistence;
using MageRide.Shared.Http;
using MageRide.TestKit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace MageRide.Safety.Tests.Infrastructure;

/// <summary>
/// One Postgres and one Redis shared by every integration test in this assembly.
/// </summary>
/// <remarks>
/// Both are load-bearing. Postgres carries the guarded resolve that makes two moderators one
/// decision and the tally that makes the third confirmation the delisting; Redis carries D-34's
/// 60/min per token and per IP, which is a limit on nothing if it is per process.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class SafetyCollection
    : ICollectionFixture<PostgresFixture>, ICollectionFixture<RedisFixture>
{
    public const string Name = "mageride-safety";
}

/// <summary>A running safety-svc and a running notification-svc, both on real sockets.</summary>
internal sealed class SafetyHarness : IAsyncDisposable
{
    public const string InternalApiKey = "c052-safety-internal-key-not-a-secret";

    public const string NotificationInternalApiKey = "c052-notification-internal-key-not-a-secret";

    /// <summary>Where a minted share token is appended. Asserted against, so it is a constant.</summary>
    public const string ShareBaseUrl = "https://passenger.mageride.test/track?token=";

    /// <summary>09:00 UTC on 30 July 2026 — 14:30 in Colombo.</summary>
    public static readonly DateTimeOffset DefaultNow = new(2026, 7, 30, 9, 0, 0, TimeSpan.Zero);

    private readonly WebApplication _safety;
    private readonly WebApplication _notification;
    private readonly PostgresFixture _postgres;

    private SafetyHarness(
        WebApplication safety,
        WebApplication notification,
        PostgresFixture postgres,
        TestTokenIssuer tokens,
        FakeTimeProvider clock,
        ContentStub content,
        SmsGatewayStub primarySms,
        SmsGatewayStub secondarySms)
    {
        _safety = safety;
        _notification = notification;
        _postgres = postgres;

        Tokens = tokens;
        Clock = clock;
        Content = content;
        PrimarySms = primarySms;
        SecondarySms = secondarySms;

        Client = new HttpClient { BaseAddress = new Uri(AddressOf(safety)), Timeout = TimeSpan.FromSeconds(120) };
        Seed = new SafetySeed(postgres);
    }

    public HttpClient Client { get; }

    public TestTokenIssuer Tokens { get; }

    /// <summary>
    /// safety-svc's clock. <b>notification-svc runs on the real one</b>, deliberately: the D-33
    /// measurement is wall-clock latency across two processes and two sockets, and a fake clock on
    /// either side would measure nothing.
    /// </summary>
    public FakeTimeProvider Clock { get; }

    public ContentStub Content { get; }

    public SmsGatewayStub PrimarySms { get; }

    public SmsGatewayStub SecondarySms { get; }

    public SafetySeed Seed { get; }

    public IReadOnlyList<SentSms> AllSms => [.. PrimarySms.Sent.Concat(SecondarySms.Sent)];

    public static async Task<SafetyHarness> StartAsync(
        PostgresFixture postgres,
        RedisFixture redis,
        IDictionary<string, string?>? settings = null,
        bool withSecondaryGateway = true,
        bool withNotificationService = true)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        ArgumentNullException.ThrowIfNull(redis);

        postgres.RequireAvailable();
        redis.RequireAvailable();

        await postgres.EnsureMigratedAsync();
        await ResetAsync(postgres);

        var tokens = new TestTokenIssuer();
        var clock = new FakeTimeProvider(DefaultNow);

        var content = await ContentStub.StartAsync();
        var primarySms = await SmsGatewayStub.StartPrimaryAsync();
        var secondarySms = await SmsGatewayStub.StartSecondaryAsync();

        var notification = BuildNotification(postgres, redis, content, primarySms, secondarySms, withSecondaryGateway);
        await notification.StartAsync();

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            ["Postgres:PgBouncerTransactionMode"] = "false",
            ["ConnectionStrings:Redis"] = redis.ConnectionString,
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = tokens.IssuerName,
            ["Jwt:RequireHttpsMetadata"] = "false",

            ["Safety:InternalApiKey"] = InternalApiKey,
            ["Safety:ShareBaseUrl"] = ShareBaseUrl,
            ["Safety:NotificationBaseUrl"] = withNotificationService ? AddressOf(notification) : null,
            ["Safety:NotificationInternalApiKey"] = NotificationInternalApiKey,

            // The gRPC counter hop is off unless a test turns it on: reputation-svc is not running,
            // and a client dialling a dead address would make every report log a two-second timeout.
            // The report itself, its row and its outbox event are what this suite is about.
            ["Safety:ReputationReportingEnabled"] = "false",

            // No outbox *dispatcher*: it wants a broker, and nothing here consumes safety.events.
            // The row is still written — that is the point, and it is what this suite asserts: the
            // admin live feed commits inside the transaction that records the alert.
            ["Outbox:DispatcherEnabled"] = "false",

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

        var safety = SafetyApplication.Build(
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

                builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                    .PostConfigure(bearer =>
                    {
                        bearer.ConfigurationManager = null;
                        bearer.TokenValidationParameters.IssuerSigningKey = tokens.PublicKey;
                        bearer.TokenValidationParameters.IssuerSigningKeyResolver = null;
                    });
            });

        await safety.StartAsync();

        return new SafetyHarness(safety, notification, postgres, tokens, clock, content, primarySms, secondarySms);
    }

    /// <summary>
    /// A real notification-svc (C051), because D-33 is a property of the two services together.
    /// </summary>
    /// <remarks>
    /// Its background workers are off: an SOS is dispatched <em>inline</em> on the send request
    /// (that is what the SLO buys), so a delivery worker draining the queue behind it would make
    /// "the alert went out on this call" indistinguishable from "a worker picked it up".
    /// </remarks>
    private static WebApplication BuildNotification(
        PostgresFixture postgres,
        RedisFixture redis,
        ContentStub content,
        SmsGatewayStub primarySms,
        SmsGatewayStub secondarySms,
        bool withSecondaryGateway) =>
        NotificationApplication.Build(
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

                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
                    ["Postgres:PgBouncerTransactionMode"] = "false",
                    ["ConnectionStrings:Redis"] = redis.ConnectionString,
                    ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
                    ["Jwt:Issuer"] = "https://iam.mageride.test",
                    ["Jwt:RequireHttpsMetadata"] = "false",

                    ["Notification:InternalApiKey"] = NotificationInternalApiKey,
                    ["Notification:ContentBaseUrl"] = content.BaseAddress,
                    ["Notification:WebTrackBaseUrl"] = ShareBaseUrl,
                    ["Notification:DeliveryEnabled"] = "false",
                    ["Notification:OfferAckSweepEnabled"] = "false",
                    ["Notification:RetentionSweepEnabled"] = "false",
                    ["Notification:ConsumersEnabled"] = "false",

                    ["Sms:Provider"] = "fitsms",
                    ["Sms:FitSmsBaseUrl"] = primarySms.BaseAddress.TrimEnd('/') + "/api/v4/",
                    ["Sms:FitSmsApiToken"] = "test-key",
                    ["Sms:SecondaryGateway"] = withSecondaryGateway ? secondarySms.BaseAddress : null,
                    ["Sms:SecondaryApiKey"] = withSecondaryGateway ? "secondary-key" : null,

                    ["urls"] = "http://127.0.0.1:0",
                    ["Otel:PrometheusEnabled"] = "false",
                });
            });

    // -----------------------------------------------------------------------------------------
    // HTTP
    // -----------------------------------------------------------------------------------------

    public T Resolve<T>() where T : notnull => _safety.Services.GetRequiredService<T>();

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

    public async Task<HttpResponseMessage> DeleteAsync(string path, string bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        return await Client.SendAsync(request);
    }

    public async Task<HttpResponseMessage> GetAsync(string path, string? bearer = null, string? clientIp = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (clientIp is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", clientIp);
        }

        return await Client.SendAsync(request);
    }

    /// <summary>The internal plane, called the way admin-bff or ride-svc would.</summary>
    public async Task<HttpResponseMessage> InternalAsync(
        HttpMethod method, string path, object? body = null, string? apiKey = InternalApiKey)
    {
        using var request = new HttpRequestMessage(method, path);

        if (apiKey is not null)
        {
            request.Headers.TryAddWithoutValidation(InternalSafetyEndpoints.ApiKeyHeader, apiKey);
        }

        if (method == HttpMethod.Post)
        {
            request.Headers.TryAddWithoutValidation(MageRideHeaders.IdempotencyKey, Guid.NewGuid().ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: MageRideJson.Options);
        }

        return await Client.SendAsync(request);
    }

    public static async Task<T> OkAsync<T>(HttpResponseMessage response, string what)
    {
        ArgumentNullException.ThrowIfNull(response);

        var text = await response.Content.ReadAsStringAsync();

        Assert.True((int)response.StatusCode is >= 200 and < 300, $"{what} returned {(int)response.StatusCode}: {text}");

        response.Dispose();

        return JsonSerializer.Deserialize<T>(text, MageRideJson.Options)!;
    }

    public static async Task<(string Code, string Body)> ProblemAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var text = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(text);

        var type = document.RootElement.TryGetProperty("type", out var value) ? value.GetString() ?? string.Empty : string.Empty;

        return (type.Split('/')[^1], text);
    }

    // -----------------------------------------------------------------------------------------
    // Asserting against the rows
    // -----------------------------------------------------------------------------------------

    public Task<NpgsqlConnection> OpenAsync() => _postgres.OpenAsync();

    /// <summary>
    /// A connection from the service's own factory, with the PostGIS type mapping the kernel
    /// installs (<c>UseNetTopologySuite</c>).
    /// </summary>
    /// <remarks>
    /// The fixture's raw connection has no geography mapping, so a query that binds a
    /// <c>GeoPoint</c> parameter — dispatch-svc's candidate narrow, for one — fails on it. Using the
    /// service's factory is also the more honest arrangement: the query under test runs against the
    /// data source the platform configures.
    /// </remarks>
    public Task<NpgsqlConnection> OpenServiceConnectionAsync() =>
        Resolve<MageRide.Shared.Persistence.INpgsqlConnectionFactory>().OpenAsync();

    public async Task<SosEvent?> SosAsync(Guid id)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.QuerySingleOrDefaultAsync<SosEvent>(
            """
            SELECT id, user_id, role, ride_id, lat, lng, emergency_contact, sms_status,
                   primary_gateway, secondary_gateway, admin_acked_at, source, share_token, ts, dispatched_at
              FROM safety.sos_events WHERE id = @Id;
            """,
            new { Id = id });
    }

    /// <summary>Everything on <c>safety.outbox</c>, oldest first — the admin live feed.</summary>
    public async Task<IReadOnlyList<(string EventType, string Payload)>> OutboxAsync()
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<(string, string)>(
            "SELECT event_type, payload::text FROM safety.outbox ORDER BY id;");

        return [.. rows];
    }

    public async Task<IReadOnlyList<ShareToken>> ShareTokensAsync()
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<ShareToken>(
            """
            SELECT token, trip_id, scope, location_request_id, expires_at, revoked_at,
                   last_access_at, access_count, created_at
              FROM safety.trip_share_tokens ORDER BY created_at;
            """);

        return [.. rows];
    }

    public async Task<IReadOnlyList<VehicleReport>> ReportsAsync()
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<VehicleReport>(
            """
            SELECT id, reporter_id, vehicle_id, ride_id, driver_id, reason, status, created_at,
                   resolved_at, resolved_by, resolution_note
              FROM safety.vehicle_reports ORDER BY created_at;
            """);

        return [.. rows];
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        foreach (var app in new[] { _safety, _notification })
        {
            try
            {
                await app.StopAsync(TimeSpan.FromSeconds(10));
                await app.DisposeAsync();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"warning: could not stop a harness app: {exception.Message}");
            }
        }

        await Content.DisposeAsync();
        await PrimarySms.DisposeAsync();
        await SecondarySms.DisposeAsync();
    }

    private static string AddressOf(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

    /// <summary>Empties what this service owns, plus the rows its tests create in other schemas.</summary>
    private static async Task ResetAsync(PostgresFixture postgres)
    {
        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            TRUNCATE safety.sos_events, safety.trip_share_tokens, safety.vehicle_reports,
                     safety.blocked_drivers, safety.location_request_audit,
                     safety.outbox, safety.command_log CASCADE;
            TRUNCATE comms.notifications, comms.notification_tokens, comms.command_log CASCADE;
            TRUNCATE dispatch.driver_presence CASCADE;
            TRUNCATE rides.rides CASCADE;
            TRUNCATE registry.vehicles CASCADE;
            TRUNCATE iam.users CASCADE;
            """);
    }
}
