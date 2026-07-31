using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using MageRide.Notification.Domain;
using MageRide.Notification.Endpoints;
using MageRide.Notification.Messaging;
using MageRide.Notification.Persistence;
using MageRide.Notification.Push;
using MageRide.Notification.Sending;
using MageRide.Shared.Http;
using MageRide.TestKit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace MageRide.Notification.Tests.Infrastructure;

/// <summary>
/// One Postgres and one Redis shared by every integration test in this assembly.
/// </summary>
/// <remarks>
/// <b>Both are load-bearing rather than convenient.</b> Postgres carries E-01's exactly-once — the
/// fallback is a guarded <c>UPDATE</c> and <c>ux_notifications_dedupe</c>, not application care —
/// and Redis carries P-12's buckets, which have to be shared across replicas or the limit is a
/// limit on nothing. A fake for either would prove that the code calls what it calls.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class NotificationCollection
    : ICollectionFixture<PostgresFixture>, ICollectionFixture<RedisFixture>
{
    public const string Name = "mageride-notification";
}

/// <summary>A running notification-svc on a real socket, against a real Postgres and Redis.</summary>
internal sealed class NotificationHarness : IAsyncDisposable
{
    /// <summary>The interim shared secret this service's internal plane demands.</summary>
    public const string InternalApiKey = "c051-notification-internal-key-not-a-secret";

    /// <summary>Where a minted share token is appended. Asserted against, so it is a constant.</summary>
    public const string WebTrackBaseUrl = "https://passenger.mageride.test/track?token=";

    /// <summary>09:00 UTC on 30 July 2026 — 14:30 in Colombo.</summary>
    public static readonly DateTimeOffset DefaultNow = new(2026, 7, 30, 9, 0, 0, TimeSpan.Zero);

    private readonly WebApplication _app;
    private readonly PostgresFixture _postgres;

    private NotificationHarness(
        WebApplication app,
        PostgresFixture postgres,
        TestTokenIssuer tokens,
        FakeTimeProvider clock,
        RecordingPushChannel pushes,
        ContentStub content,
        SmsGatewayStub primarySms,
        SmsGatewayStub secondarySms)
    {
        _app = app;
        _postgres = postgres;

        Tokens = tokens;
        Clock = clock;
        Pushes = pushes;
        Content = content;
        PrimarySms = primarySms;
        SecondarySms = secondarySms;

        Client = new HttpClient { BaseAddress = new Uri(AddressOf(app)), Timeout = TimeSpan.FromSeconds(120) };
        Seed = new NotificationSeed(postgres);
    }

    public HttpClient Client { get; }

    public TestTokenIssuer Tokens { get; }

    public FakeTimeProvider Clock { get; }

    public RecordingPushChannel Pushes { get; }

    public ContentStub Content { get; }

    public SmsGatewayStub PrimarySms { get; }

    public SmsGatewayStub SecondarySms { get; }

    public NotificationSeed Seed { get; }

    /// <summary>Everything either gateway was asked to deliver.</summary>
    public IReadOnlyList<SentSms> AllSms => [.. PrimarySms.Sent.Concat(SecondarySms.Sent)];

    public static async Task<NotificationHarness> StartAsync(
        PostgresFixture postgres,
        RedisFixture redis,
        IDictionary<string, string?>? settings = null,
        DateTimeOffset? now = null,
        bool withSecondaryGateway = true)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        ArgumentNullException.ThrowIfNull(redis);

        postgres.RequireAvailable();
        redis.RequireAvailable();

        await postgres.EnsureMigratedAsync();
        await ResetAsync(postgres);

        var tokens = new TestTokenIssuer();
        var clock = new FakeTimeProvider(now ?? DefaultNow);
        var pushes = new RecordingPushChannel();

        var content = await ContentStub.StartAsync();
        var primarySms = await SmsGatewayStub.StartPrimaryAsync();
        var secondarySms = await SmsGatewayStub.StartSecondaryAsync();

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            ["Postgres:PgBouncerTransactionMode"] = "false",
            ["ConnectionStrings:Redis"] = redis.ConnectionString,
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = tokens.IssuerName,
            ["Jwt:RequireHttpsMetadata"] = "false",

            ["Notification:InternalApiKey"] = InternalApiKey,
            ["Notification:ContentBaseUrl"] = content.BaseAddress,
            ["Notification:ContentInternalApiKey"] = ContentStub.InternalApiKey,
            ["Notification:WebTrackBaseUrl"] = WebTrackBaseUrl,

            // The three background loops are off and driven by hand. A pass running under an
            // assertion would make "the sweep found it" indistinguishable from "a previous pass
            // did" — the same reason Fare.Api.Tests turns its nudge sweep off.
            ["Notification:DeliveryEnabled"] = "false",
            ["Notification:OfferAckSweepEnabled"] = "false",
            ["Notification:RetentionSweepEnabled"] = "false",

            // Off: this suite drives the handlers directly. A consumer polling a broker that is not
            // there would log an error a second for the length of the run.
            ["Notification:ConsumersEnabled"] = "false",

            // The SMS gateways are real sockets — D-33's parallel dispatch is the claim.
            ["Sms:Provider"] = "notifylk",
            ["Sms:NotifyLkBaseUrl"] = primarySms.BaseAddress.TrimEnd('/') + "/api/v1/",
            ["Sms:NotifyLkUserId"] = "test-user",
            ["Sms:NotifyLkApiKey"] = "test-key",
            ["Sms:SecondaryGateway"] = withSecondaryGateway ? secondarySms.BaseAddress : null,
            ["Sms:SecondaryApiKey"] = withSecondaryGateway ? "secondary-key" : null,

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

        var app = NotificationApplication.Build(
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

                // Registered here — before AddNotificationServices — so it is first in the
                // IEnumerable<IPushChannel> and wins the platform lookup over the log transport.
                builder.Services.AddSingleton<IPushChannel>(pushes);

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

        return new NotificationHarness(app, postgres, tokens, clock, pushes, content, primarySms, secondarySms);
    }

    // -----------------------------------------------------------------------------------------
    // Driving the pipeline
    // -----------------------------------------------------------------------------------------

    public T Resolve<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    /// <summary>One delivery pass — D-27's worker, driven by hand.</summary>
    public Task<int> DeliverAsync() => Resolve<DeliveryWorker>().DrainAsync(CancellationToken.None);

    /// <summary>One E-01 ack sweep.</summary>
    public Task<int> SweepUnackedOffersAsync() => Resolve<OfferAckWorker>().SweepAsync(CancellationToken.None);

    /// <summary>Feeds one event to the handler the consumer would resolve for it.</summary>
    public async Task HandleAsync<THandler>(string topicKey, string eventType, object payload)
        where THandler : notnull, IEventHandler
    {
        await using var scope = _app.Services.CreateAsyncScope();

        var handler = scope.ServiceProvider.GetRequiredService<THandler>();
        var envelope = EventEnvelopeFactory.Build(topicKey, eventType, payload);

        try
        {
            await handler.HandleAsync(envelope, CancellationToken.None);
        }
        finally
        {
            envelope.Dispose();
        }
    }

    /// <summary>The internal send route, called the way another service would.</summary>
    public async Task<HttpResponseMessage> SendInternalAsync(object body, string? apiKey = InternalApiKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/internal/notify/send")
        {
            Content = JsonContent.Create(body, options: MageRideJson.Options),
        };

        if (apiKey is not null)
        {
            request.Headers.TryAddWithoutValidation(InternalNotifyEndpoints.ApiKeyHeader, apiKey);
        }

        request.Headers.TryAddWithoutValidation(MageRideHeaders.IdempotencyKey, Guid.NewGuid().ToString());

        return await Client.SendAsync(request);
    }

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

    /// <summary>
    /// A PUT with a body written as literal JSON.
    /// </summary>
    /// <remarks>
    /// <b>Not <c>JsonContent.Create(…, MageRideJson.Options)</c>, and that is the point.</b> The
    /// kernel's options camelCase dictionary keys on the way out, so a client that serialised
    /// <c>{"LOW_BALANCE": false}</c> through them would send <c>loW_BALANCE</c> — the exact
    /// corruption <c>LiteralKeyDictionaryConverter</c> exists to stop on the server's side of the
    /// wire. Writing the body by hand is what lets these tests assert the *server's* behaviour
    /// rather than the test client's.
    /// </remarks>
    public async Task<HttpResponseMessage> PutJsonAsync(string path, string json, string bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        return await Client.SendAsync(request);
    }

    /// <summary>US-10.7's switches, written as the literal JSON a correct client sends.</summary>
    public Task<HttpResponseMessage> SetPreferencesAsync(
        IReadOnlyDictionary<string, bool> preferences, string bearer)
    {
        var switches = string.Join(
            ',', preferences.Select(pair => $"\"{pair.Key}\":{(pair.Value ? "true" : "false")}"));

        return PutJsonAsync("/v1/notify/preferences", $"{{\"preferences\":{{{switches}}}}}", bearer);
    }

    public static async Task<T> OkAsync<T>(HttpResponseMessage response, string what)
    {
        ArgumentNullException.ThrowIfNull(response);

        var text = await response.Content.ReadAsStringAsync();

        Assert.True((int)response.StatusCode is >= 200 and < 300, $"{what} returned {(int)response.StatusCode}: {text}");

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

    public INotificationRepository Notifications => Resolve<INotificationRepository>();

    /// <summary>Every notification queued for one recipient, oldest first.</summary>
    public async Task<IReadOnlyList<NotificationRow>> QueueAsync(Guid? recipientUserId = null)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<NotificationRow>(
            """
            SELECT id, dedupe_key, notification_type, template_key, channel, recipient_user_id, recipient_phone,
                   language, priority, payload::text AS payload, status, attempts, next_attempt_at,
                   ack_deadline_at, acked_at, fallback_of, created_at
              FROM comms.notifications
             WHERE (@RecipientUserId::uuid IS NULL OR recipient_user_id = @RecipientUserId)
             ORDER BY created_at, dedupe_key;
            """,
            new { RecipientUserId = recipientUserId });

        return [.. rows];
    }

    /// <summary>The AL-44 tokens minted so far. Read from the row, because no API returns one.</summary>
    public async Task<IReadOnlyList<(string Token, string Scope, Guid? TripId, Guid? LocationRequestId)>>
        ShareTokensAsync()
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<(string, string, Guid?, Guid?)>(
            """
            SELECT token, scope, trip_id, location_request_id
              FROM safety.trip_share_tokens ORDER BY created_at;
            """);

        return [.. rows];
    }

    public async Task<IReadOnlyDictionary<string, bool>> PreferencesAsync(Guid userId)
    {
        await using var connection = await _postgres.OpenAsync();

        var json = await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT notif_prefs::text FROM iam.users WHERE id = @Id;", new { Id = userId });

        if (string.IsNullOrWhiteSpace(json))
        {
            return NotificationRecipient.NoPreferences;
        }

        using var document = JsonDocument.Parse(json);
        var values = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var property in document.RootElement.EnumerateObject())
        {
            values[property.Name] = property.Value.GetBoolean();
        }

        return values;
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
            Console.Error.WriteLine($"warning: could not stop the notification harness: {exception.Message}");
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
            TRUNCATE comms.notifications, comms.notification_tokens, comms.command_log CASCADE;
            TRUNCATE safety.trip_share_tokens CASCADE;
            TRUNCATE rides.location_requests CASCADE;
            TRUNCATE iam.users CASCADE;
            """);
    }
}
