using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using MageRide.Content.Endpoints;
using MageRide.Shared.Http;
using MageRide.TestKit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace MageRide.Content.Tests.Infrastructure;

/// <summary>
/// A running content-svc on a real socket, against a real Postgres and a real Redis.
/// </summary>
/// <remarks>
/// <para>
/// Built through <see cref="ContentApplication.Build"/>, so the pipeline under test — the bearer
/// handler, the problem+json handler, the options validation, the internal-key filter, the cache — is
/// the one the process runs.
/// </para>
/// <para>
/// <b>The clock is a <see cref="FakeTimeProvider"/>.</b> This component's third definition of done is
/// written in terms of a 300-second cache TTL and no suite can wait for it; the cache measures expiry
/// on <see cref="TimeProvider"/> exactly so that advancing this clock genuinely advances the service's
/// view of the TTL rather than approximating it. It is also what lets a broadcast be scheduled in the
/// future and then become live.
/// </para>
/// <para>
/// <b>Nothing here resets the seeded content</b> — migrations 1902 and 1903 are the day-0 templates,
/// FAQ and carousel slides, and they are what the read tests assert. Test-created rows are namespaced
/// (<see cref="NextTemplateKey"/>, <see cref="NextCityCode"/>) so two tests in the shared collection
/// cannot see each other's, and <see cref="ResetAsync"/> removes only those.
/// </para>
/// </remarks>
internal sealed class ContentHarness : IAsyncDisposable
{
    /// <summary>The interim shared secret the internal plane demands until the mesh lands.</summary>
    public const string InternalApiKey = "c045-content-internal-key-not-a-secret";

    private static int _keyCounter = Random.Shared.Next(1_000, 9_000);

    private readonly WebApplication _app;
    private readonly PostgresFixture _postgres;

    private ContentHarness(
        WebApplication app, PostgresFixture postgres, TestTokenIssuer tokens, FakeTimeProvider clock)
    {
        _app = app;
        _postgres = postgres;
        Tokens = tokens;
        Clock = clock;

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        Client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(120) };
    }

    public HttpClient Client { get; }

    public TestTokenIssuer Tokens { get; }

    public FakeTimeProvider Clock { get; }

    public IServiceProvider Services => _app.Services;

    /// <summary>A template key no other test in this run will use.</summary>
    public static string NextTemplateKey() =>
        "c045_key_" + Interlocked.Increment(ref _keyCounter).ToString("D5", CultureInfo.InvariantCulture);

    /// <summary>An operating-city code no other test in this run will use.</summary>
    public static string NextCityCode() =>
        "c045_city_" + Interlocked.Increment(ref _keyCounter).ToString("D5", CultureInfo.InvariantCulture);

    public static async Task<ContentHarness> StartAsync(
        PostgresFixture postgres,
        RedisFixture redis,
        IDictionary<string, string?>? settings = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        ArgumentNullException.ThrowIfNull(redis);

        postgres.RequireAvailable();
        redis.RequireAvailable();
        await postgres.EnsureMigratedAsync();
        await ResetAsync(postgres);

        var tokens = new TestTokenIssuer();
        var clock = new FakeTimeProvider(now ?? new DateTimeOffset(2026, 7, 30, 9, 0, 0, TimeSpan.Zero));

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            // The container is plain Postgres, not PgBouncer.
            ["Postgres:PgBouncerTransactionMode"] = "false",
            ["ConnectionStrings:Redis"] = redis.ConnectionString,
            // Never fetched — the bearer handler is pointed at the test key below. The kernel's auth
            // wiring binds the setting all the same, so it has to be present and parseable.
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = tokens.IssuerName,
            ["Jwt:RequireHttpsMetadata"] = "false",
            ["Content:InternalApiKey"] = InternalApiKey,
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

        var app = ContentApplication.Build(
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

                // Ahead of AddMageRideDefaults's TryAddSingleton, so the cache's TTL arithmetic and
                // every publish timestamp run on the test's clock.
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

        return new ContentHarness(app, postgres, tokens, clock);
    }

    // -----------------------------------------------------------------------------------------
    // HTTP
    // -----------------------------------------------------------------------------------------

    // Every one of these awaits inside the `using`: returning the un-awaited task would dispose the
    // request — and its content — while it was still being written to the socket.
    public async Task<HttpResponseMessage> GetAsync(
        string path, string? bearer = null, string? internalKey = null)
    {
        using var request = Request(HttpMethod.Get, path, bearer, internalKey);

        return await Client.SendAsync(request);
    }

    /// <summary>GETs and asserts a 200, returning the body. A failure prints the problem document.</summary>
    public async Task<JsonElement> GetJsonAsync(string path, string? bearer = null, string? internalKey = null)
    {
        using var response = await GetAsync(path, bearer, internalKey);
        var text = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == System.Net.HttpStatusCode.OK,
            $"GET {path} returned {(int)response.StatusCode}: {text}");

        using var document = JsonDocument.Parse(text);

        return document.RootElement.Clone();
    }

    /// <summary>Reads a 200 body into <typeparamref name="T"/>.</summary>
    public async Task<T> GetAsync<T>(string path, string? bearer = null, string? internalKey = null)
    {
        using var response = await GetAsync(path, bearer, internalKey);
        var text = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == System.Net.HttpStatusCode.OK,
            $"GET {path} returned {(int)response.StatusCode}: {text}");

        return JsonSerializer.Deserialize<T>(text, MageRideJson.Options)!;
    }

    public async Task<HttpResponseMessage> PutAsync(string path, object body, string bearer)
    {
        using var request = Request(HttpMethod.Put, path, bearer, internalKey: null);
        request.Content = JsonContent.Create(body, options: MageRideJson.Options);

        return await Client.SendAsync(request);
    }

    /// <summary>POSTs with a fresh <c>Idempotency-Key</c> — what a well-behaved client does once.</summary>
    public Task<HttpResponseMessage> PostAsync(
        string path, object? body, string? bearer = null, string? internalKey = null) =>
        PostWithKeyAsync(path, body, bearer, Guid.NewGuid().ToString(), internalKey);

    /// <summary>POSTs with a caller-chosen key, so a retry can be replayed (R-14).</summary>
    public async Task<HttpResponseMessage> PostWithKeyAsync(
        string path,
        object? body,
        string? bearer,
        string idempotencyKey,
        string? internalKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        using var request = Request(HttpMethod.Post, path, bearer, internalKey);

        request.Headers.TryAddWithoutValidation(MageRideHeaders.IdempotencyKey, idempotencyKey);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: MageRideJson.Options);
        }

        return await Client.SendAsync(request);
    }

    /// <summary>The RFC 7807 <c>type</c> code and the <c>errors</c> extension of a failed response.</summary>
    public static async Task<(string Code, JsonElement Body)> ProblemAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var text = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement.Clone();

        var type = root.TryGetProperty("type", out var value) ? value.GetString() ?? string.Empty : string.Empty;

        return (type.Split('/')[^1], root);
    }

    private static HttpRequestMessage Request(
        HttpMethod method, string path, string? bearer, string? internalKey)
    {
        var request = new HttpRequestMessage(method, path);

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (internalKey is not null)
        {
            request.Headers.TryAddWithoutValidation(ContentEndpoints.ApiKeyHeader, internalKey);
        }

        return request;
    }

    // -----------------------------------------------------------------------------------------
    // Seeding. content-svc creates no accounts — iam-svc does — and the day-0 content is the
    // migration set's.
    // -----------------------------------------------------------------------------------------

    /// <summary>An <c>iam.users</c> row plus a bearer for it, so <c>created_by</c>'s FK is satisfiable.</summary>
    public async Task<(Guid Id, string Bearer)> CreateAdminAsync(bool superAdmin = false)
    {
        var id = await CreateUserAsync(superAdmin ? "super_admin" : "admin");

        return (id, superAdmin
            ? Tokens.Issue(id, ["super_admin"], "admin")
            : Tokens.Admin(id));
    }

    public async Task<Guid> CreateUserAsync(string role)
    {
        var id = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO iam.users (id, phone, role) VALUES (@Id, @Phone, @Role);",
            new
            {
                Id = id,
                Phone = "+9477" + Random.Shared.NextInt64(1_000_000, 9_999_999).ToString(CultureInfo.InvariantCulture),
                Role = role,
            });

        return id;
    }

    /// <summary>
    /// An operating city, as admin-bff would write one (<c>POST /v1/admin/config/cities</c>, C065).
    /// </summary>
    public async Task<string> CreateCityAsync(
        string? code = null, bool active = true, int sortOrder = 90, double lat = 7.0, double lng = 80.0)
    {
        var cityCode = code ?? NextCityCode();

        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO config.operating_cities
                  (code, name_en, name_si, name_ta, centroid_lat, centroid_lng, is_active, sort_order)
            VALUES (@Code, @NameEn, 'පරීක්ෂණ', 'சோதனை', @Lat, @Lng, @Active, @SortOrder);
            """,
            new
            {
                Code = cityCode,
                NameEn = cityCode,
                Lat = lat,
                Lng = lng,
                Active = active,
                SortOrder = sortOrder,
            });

        return cityCode;
    }

    /// <summary>A published trilingual template, written the way a migration seed does.</summary>
    public async Task SeedTemplateAsync(string key, string? subject = null, string body = "Hello {{name}}")
    {
        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO content.notification_templates (template_key, language, subject, body)
            VALUES (@Key, 'si', @Subject, @Body), (@Key, 'ta', @Subject, @Body), (@Key, 'en', @Subject, @Body);
            """,
            new { Key = key, Subject = subject, Body = body });
    }

    /// <summary>Opens a connection on the test database — for asserting against the schema directly.</summary>
    public Task<NpgsqlConnection> OpenAsync() => _postgres.OpenAsync();

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
    /// Removes what earlier tests created, and nothing the migrations seeded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The four seeded template keys, the twelve FAQ rows, the three launch cities and the six
    /// carousel slides are this component's day-0 content and are what the read tests assert against —
    /// truncating them would leave the suite testing fixtures of its own making.
    /// </para>
    /// <para>
    /// <b>Broadcasts are the one table emptied</b>, because no migration seeds one, every row is some
    /// test's, and a leftover announcement is a real member of the next test's banner list. Templates
    /// and cities are namespaced per test instead (<see cref="NextTemplateKey"/>,
    /// <see cref="NextCityCode"/>) — deleting them here would race the tests that insert a row and
    /// then start a second service.
    /// </para>
    /// </remarks>
    private static async Task ResetAsync(PostgresFixture postgres)
    {
        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            DELETE FROM content.broadcasts;
            DELETE FROM content.command_log;
            """);
    }
}
