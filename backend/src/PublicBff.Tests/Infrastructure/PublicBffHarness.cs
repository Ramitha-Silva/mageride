using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using MageRide.PublicBff.Endpoints;
using MageRide.Ride;
using MageRide.Safety;
using MageRide.Shared.Http;
using MageRide.TestKit;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using StackExchange.Redis;

namespace MageRide.PublicBff.Tests.Infrastructure;

/// <summary>
/// One Postgres and one Redis shared by every integration test in this assembly.
/// </summary>
/// <remarks>
/// Both are load-bearing. Postgres carries the share token, the ride behind it and the
/// <c>rides.location_requests</c> row a web confirm has to move — the last of which is the whole of
/// AL-45. Redis carries the per-token and per-IP buckets, which are a limit on nothing if they are
/// per process, plus position-processor-svc's <c>veh:meta</c> fix and the delivery code
/// notification-svc leaves for SCR-WT-002.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class PublicBffCollection
    : ICollectionFixture<PostgresFixture>, ICollectionFixture<RedisFixture>
{
    public const string Name = "mageride-public-bff";
}

/// <summary>A running public-bff with a real ride-svc and a real safety-svc behind it.</summary>
internal sealed class PublicBffHarness : IAsyncDisposable
{
    public const string RideInternalApiKey = "c066-ride-internal-key-not-a-secret";

    public const string SafetyInternalApiKey = "c066-safety-internal-key-not-a-secret";

    public const string NotificationInternalApiKey = "c066-notification-internal-key-not-a-secret";

    /// <summary>
    /// public-bff's clock, and it starts at the real one.
    /// </summary>
    /// <remarks>
    /// <b>A pinned instant would have made half this suite a lie.</b> The 300 s location-request
    /// window is decided by Postgres — <c>issued_at + make_interval(secs => ttl_seconds) &gt; now()</c>,
    /// so that "a caller's clock, and not the database's, cannot decide it" (C037) — and safety-svc
    /// dispatches on the wall clock too. A fake clock set to a fixed date would put every seeded row
    /// outside a window the database evaluates against today, and the failures would look like
    /// contract bugs. So the fake provider starts at real UTC and the rows are seeded relative to it;
    /// what the fake still buys is a clock the test can <em>advance</em>, which is how the stream's
    /// tick is driven.
    /// <para>
    /// Truncated to the second because Postgres stores <c>timestamptz</c> at microsecond precision
    /// and a round trip would otherwise lose the sub-microsecond part of a .NET tick.
    /// </para>
    /// </remarks>
    public DateTimeOffset Now { get; }

    private readonly WebApplication _publicBff;
    private readonly WebApplication _ride;
    private readonly WebApplication _safety;
    private readonly PostgresFixture _postgres;

    private PublicBffHarness(
        WebApplication publicBff,
        WebApplication ride,
        WebApplication safety,
        PostgresFixture postgres,
        IConnectionMultiplexer redis,
        FakeTimeProvider clock,
        NotificationStub notifications)
    {
        _publicBff = publicBff;
        _ride = ride;
        _safety = safety;
        _postgres = postgres;

        Clock = clock;
        Now = clock.GetUtcNow();
        Notifications = notifications;
        Redis = redis;

        Client = new HttpClient { BaseAddress = new Uri(AddressOf(publicBff)), Timeout = TimeSpan.FromSeconds(120) };
        Seed = new PublicBffSeed(postgres, redis);
    }

    public HttpClient Client { get; }

    /// <summary>
    /// public-bff's clock. <b>ride-svc and safety-svc run on the real one</b>, deliberately: the
    /// 300 s location-request window and the D-33 dispatch are their measurements, and a fake clock
    /// on one side of an HTTP hop measures nothing.
    /// </summary>
    public FakeTimeProvider Clock { get; }

    public NotificationStub Notifications { get; }

    public IConnectionMultiplexer Redis { get; }

    public PublicBffSeed Seed { get; }

    public static async Task<PublicBffHarness> StartAsync(
        PostgresFixture postgres,
        RedisFixture redis,
        IDictionary<string, string?>? settings = null,
        bool withRideService = true,
        bool withSafetyService = true)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        ArgumentNullException.ThrowIfNull(redis);

        postgres.RequireAvailable();
        redis.RequireAvailable();

        await postgres.EnsureMigratedAsync();
        await ResetAsync(postgres, redis);

        var utcNow = DateTimeOffset.UtcNow;
        var clock = new FakeTimeProvider(
            new DateTimeOffset(utcNow.Ticks - (utcNow.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero));

        var notifications = await NotificationStub.StartAsync();

        var ride = BuildRide(postgres);
        await ride.StartAsync();

        var safety = BuildSafety(postgres, redis, notifications);
        await safety.StartAsync();

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            ["Postgres:PgBouncerTransactionMode"] = "false",
            ["ConnectionStrings:Redis"] = redis.ConnectionString,

            ["PublicBff:Ride:BaseUrl"] = withRideService ? AddressOf(ride) : null,
            ["PublicBff:Ride:InternalApiKey"] = RideInternalApiKey,
            ["PublicBff:Safety:BaseUrl"] = withSafetyService ? AddressOf(safety) : null,
            ["PublicBff:Safety:InternalApiKey"] = SafetyInternalApiKey,

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

        var publicBff = PublicBffApplication.Build(
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
            });

        await publicBff.StartAsync();

        return new PublicBffHarness(
            publicBff,
            ride,
            safety,
            postgres,
            await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString),
            clock,
            notifications);
    }

    /// <summary>
    /// A real ride-svc (C037), because AL-45's claim is about <c>rides.location_requests</c>.
    /// </summary>
    /// <remarks>
    /// Its timer sweep is off: the 300 s expiry is a durable deadline on the row and this suite
    /// asserts what the row says, not how fast a worker notices. <c>Ride:IamBaseUrl</c> is unset,
    /// which only disables <em>issuing</em> a location request — the internal confirm/decline pair
    /// this suite drives asserts no rider identity, because an unregistered rider has none.
    /// </remarks>
    private static WebApplication BuildRide(PostgresFixture postgres) =>
        RideApplication.Build(
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
                    ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
                    ["Jwt:Issuer"] = "https://iam.mageride.test",
                    ["Jwt:RequireHttpsMetadata"] = "false",

                    ["Ride:InternalApiKey"] = RideInternalApiKey,
                    ["Ride:PhoneHashKey"] = "c066-ride-phone-hash-key-not-a-secret",
                    ["Ride:OtpPepper"] = "c066-ride-otp-pepper-not-a-secret",
                    ["Ride:TimersEnabled"] = "false",
                    ["Ride:StuckStateMetricsEnabled"] = "false",

                    // Nothing consumes ride.events here, and the dispatcher wants a broker. The
                    // address still has to be present because the kernel validates the producer's
                    // options on start; nothing dials it.
                    ["Outbox:DispatcherEnabled"] = "false",
                    ["Kafka:BootstrapServers"] = "127.0.0.1:1",

                    // ride-svc signs and verifies fare-estimate tokens with fare-svc's key. Nothing
                    // in this suite books a ride, so the value only has to satisfy the length rule.
                    ["Fare:EstimateTokenKey"] = "c066-fare-estimate-token-key-not-a-secret-0123456789",

                    ["urls"] = "http://127.0.0.1:0",
                    ["Otel:PrometheusEnabled"] = "false",
                });
            });

    /// <summary>
    /// A real safety-svc (C052), because US-25.5's claim is about <c>safety.sos_events</c>.
    /// </summary>
    private static WebApplication BuildSafety(
        PostgresFixture postgres, RedisFixture redis, NotificationStub notifications) =>
        SafetyApplication.Build(
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

                    ["Safety:InternalApiKey"] = SafetyInternalApiKey,
                    ["Safety:NotificationBaseUrl"] = notifications.BaseAddress,
                    ["Safety:NotificationInternalApiKey"] = NotificationInternalApiKey,
                    ["Safety:ReputationReportingEnabled"] = "false",

                    ["Outbox:DispatcherEnabled"] = "false",

                    ["urls"] = "http://127.0.0.1:0",
                    ["Otel:PrometheusEnabled"] = "false",
                });
            });

    // -----------------------------------------------------------------------------------------
    // HTTP — and never with an Authorization header, because nothing here would read one.
    // -----------------------------------------------------------------------------------------

    public T Resolve<T>() where T : notnull => _publicBff.Services.GetRequiredService<T>();

    public async Task<HttpResponseMessage> GetAsync(string path, string? clientIp = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        if (clientIp is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", clientIp);
        }

        return await Client.SendAsync(request);
    }

    /// <summary>Streams the response as it arrives, so an SSE body can be read while it is open.</summary>
    public async Task<HttpResponseMessage> StreamAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        return await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    public async Task<HttpResponseMessage> PostAsync(
        string path, object? body, string? idempotencyKey = null, string? clientIp = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);

        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation(MageRideHeaders.IdempotencyKey, idempotencyKey);
        }

        if (clientIp is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", clientIp);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: MageRideJson.Options);
        }

        return await Client.SendAsync(request);
    }

    public static async Task<JsonElement> OkAsync(HttpResponseMessage response, string what)
    {
        ArgumentNullException.ThrowIfNull(response);

        var text = await response.Content.ReadAsStringAsync();

        Assert.True((int)response.StatusCode is >= 200 and < 300, $"{what} returned {(int)response.StatusCode}: {text}");

        response.Dispose();

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public static async Task<(int Status, string Code, string Body)> ProblemAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var text = await response.Content.ReadAsStringAsync();
        var status = (int)response.StatusCode;

        response.Dispose();

        using var document = JsonDocument.Parse(text);

        var type = document.RootElement.TryGetProperty("type", out var value)
            ? value.GetString() ?? string.Empty
            : string.Empty;

        return (status, type.Split('/')[^1], text);
    }

    // -----------------------------------------------------------------------------------------
    // Asserting against the rows
    // -----------------------------------------------------------------------------------------

    public Task<NpgsqlConnection> OpenAsync() => _postgres.OpenAsync();

    public async Task<(int AccessCount, DateTimeOffset? LastAccessAt, DateTimeOffset? RevokedAt)> TokenMeterAsync(
        string token)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.QuerySingleAsync<(int, DateTimeOffset?, DateTimeOffset?)>(
            "SELECT access_count, last_access_at, revoked_at FROM safety.trip_share_tokens WHERE token = @Token;",
            new { Token = token });
    }

    public async Task<(string State, double? Lat, double? Lng)> LocationRequestAsync(Guid id)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.QuerySingleAsync<(string, double?, double?)>(
            """
            SELECT state,
                   ST_Y(resolved_geo::geometry) AS lat,
                   ST_X(resolved_geo::geometry) AS lng
              FROM rides.location_requests WHERE id = @Id;
            """,
            new { Id = id });
    }

    public async Task<IReadOnlyList<(Guid Id, Guid? UserId, string Source, string? ShareToken, string? Contact, double Lat, double Lng)>>
        SosEventsAsync()
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<(Guid, Guid?, string, string?, string?, double, double)>(
            """
            SELECT id, user_id, source, share_token, emergency_contact, lat, lng
              FROM safety.sos_events ORDER BY ts;
            """);

        return [.. rows];
    }

    public async Task<IReadOnlyList<string>> SafetyOutboxAsync()
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<string>("SELECT event_type FROM safety.outbox ORDER BY id;");

        return [.. rows];
    }

    /// <summary>Every route public-bff mapped — the fence tests read this rather than the source.</summary>
    public IReadOnlyList<(string Route, bool AllowsAnonymous, bool InPublicGroup)> Routes()
    {
        var source = _publicBff.Services.GetRequiredService<EndpointDataSource>();

        return
        [
            .. source.Endpoints.OfType<RouteEndpoint>()
                .Select(endpoint => (
                    Route: endpoint.RoutePattern.RawText ?? string.Empty,
                    AllowsAnonymous:
                        endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null,
                    InPublicGroup: endpoint.Metadata.GetMetadata<PublicSurfaceMarker>() is not null)),
        ];
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        foreach (var app in new[] { _publicBff, _ride, _safety })
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

        await Notifications.DisposeAsync();
        await Redis.DisposeAsync();
    }

    private static string AddressOf(WebApplication app) =>
        app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

    /// <summary>Empties what these tests write, in both stores.</summary>
    private static async Task ResetAsync(PostgresFixture postgres, RedisFixture redis)
    {
        await using (var connection = await postgres.OpenAsync())
        {
            await connection.ExecuteAsync(
                """
                TRUNCATE safety.sos_events, safety.trip_share_tokens, safety.outbox, safety.command_log,
                         safety.location_request_audit CASCADE;
                TRUNCATE rides.location_requests, rides.proof_artifacts, rides.rides, rides.outbox,
                         rides.command_log CASCADE;
                TRUNCATE fares.ride_payments CASCADE;
                TRUNCATE registry.driver_profiles, registry.vehicles CASCADE;
                TRUNCATE iam.users CASCADE;
                """);
        }

        // The buckets are per token and per IP and the fixture is shared, so a rate-limit test in
        // one class would otherwise spend the budget of a snapshot test in another.
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(
            redis.ConnectionString + ",allowAdmin=true");

        foreach (var endpoint in multiplexer.GetEndPoints())
        {
            await multiplexer.GetServer(endpoint).FlushDatabaseAsync();
        }
    }
}
