using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using MageRide.Shared.Http;
using MageRide.Support.Endpoints;
using MageRide.TestKit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace MageRide.Support.Tests.Infrastructure;

/// <summary>
/// A running support-svc on a real socket, against a real Postgres.
/// </summary>
/// <remarks>
/// Built through <see cref="SupportApplication.Build"/>, so the pipeline under test — the bearer
/// handler, the problem+json handler, the options validation, the idempotency middleware, the
/// internal-key filter — is the one the process runs.
/// </remarks>
internal sealed class SupportHarness : IAsyncDisposable
{
    /// <summary>The interim shared secret the internal plane demands until the mesh lands.</summary>
    public const string InternalApiKey = "c053-support-internal-key-not-a-secret";

    /// <summary>Asserted against, so it is a constant rather than a per-run value.</summary>
    public const string FileLinkSigningKey = "c053-support-file-link-key-not-a-secret";

    /// <summary>09:00 UTC on 30 July 2026 — 14:30 in Colombo.</summary>
    public static readonly DateTimeOffset DefaultNow = new(2026, 7, 30, 9, 0, 0, TimeSpan.Zero);

    private readonly WebApplication _app;
    private readonly PostgresFixture _postgres;
    private readonly string _screenshotRoot;

    private SupportHarness(
        WebApplication app,
        PostgresFixture postgres,
        TestTokenIssuer tokens,
        FakeTimeProvider clock,
        string screenshotRoot)
    {
        _app = app;
        _postgres = postgres;
        _screenshotRoot = screenshotRoot;

        Tokens = tokens;
        Clock = clock;

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        Client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(120) };
    }

    public HttpClient Client { get; }

    public TestTokenIssuer Tokens { get; }

    /// <summary>
    /// The service's clock. Load-bearing for one thing: the signed screenshot link expires, and no
    /// suite can wait a quarter of an hour for it.
    /// </summary>
    public FakeTimeProvider Clock { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<SupportHarness> StartAsync(
        PostgresFixture postgres,
        IDictionary<string, string?>? settings = null,
        bool withInternalPlane = true)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        postgres.RequireAvailable();
        await postgres.EnsureMigratedAsync();
        await ResetAsync(postgres);

        var tokens = new TestTokenIssuer();
        var clock = new FakeTimeProvider(DefaultNow);

        // One directory per harness, so two tests in the shared collection cannot see each other's
        // files and a leftover image cannot make a later assertion pass.
        var screenshotRoot = Path.Combine(
            Path.GetTempPath(), "mageride-c053", Guid.NewGuid().ToString("N"));

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            // The container is plain Postgres, not PgBouncer.
            ["Postgres:PgBouncerTransactionMode"] = "false",
            // Never fetched — the bearer handler is pointed at the test key below. The kernel's auth
            // wiring binds the setting all the same, so it has to be present and parseable.
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = tokens.IssuerName,
            ["Jwt:RequireHttpsMetadata"] = "false",

            ["Support:InternalApiKey"] = withInternalPlane ? InternalApiKey : null,
            ["Support:ScreenshotRoot"] = screenshotRoot,
            ["Support:FileLinkSigningKey"] = FileLinkSigningKey,

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

        var app = SupportApplication.Build(
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

                // Ahead of AddMageRideDefaults's TryAddSingleton, so the signed link's TTL runs on
                // the test's clock. **Row timestamps deliberately do not** — every one of them comes
                // from Postgres (see ITicketRepository.AppendEventAsync), which is why the queue
                // tests assert order rather than a fixed instant.
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

        return new SupportHarness(app, postgres, tokens, clock, screenshotRoot);
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

        return await OkAsync<T>(response, $"GET {path}");
    }

    /// <summary>POSTs with a fresh <c>Idempotency-Key</c> — what a well-behaved client does once.</summary>
    public Task<HttpResponseMessage> PostAsync(string path, object? body, string? bearer = null) =>
        PostWithKeyAsync(path, body, bearer, Guid.NewGuid().ToString());

    /// <summary>POSTs with a caller-chosen key, so a retry can be replayed (R-14).</summary>
    public async Task<HttpResponseMessage> PostWithKeyAsync(
        string path, object? body, string? bearer, string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        using var request = new HttpRequestMessage(HttpMethod.Post, path);

        request.Headers.TryAddWithoutValidation(MageRideHeaders.IdempotencyKey, idempotencyKey);

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: MageRideJson.Options);
        }

        return await Client.SendAsync(request);
    }

    /// <summary>The internal plane, called the way admin-bff would.</summary>
    public async Task<HttpResponseMessage> InternalAsync(
        HttpMethod method, string path, object? body = null, string? apiKey = InternalApiKey)
    {
        using var request = new HttpRequestMessage(method, path);

        if (apiKey is not null)
        {
            request.Headers.TryAddWithoutValidation(InternalSupportEndpoints.ApiKeyHeader, apiKey);
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

    public async Task<T> InternalAsync<T>(
        HttpMethod method, string path, object? body = null, string? apiKey = InternalApiKey)
    {
        using var response = await InternalAsync(method, path, body, apiKey);

        return await OkAsync<T>(response, $"{method} {path}");
    }

    /// <summary>Uploads bytes as <c>multipart/form-data</c>, the way the raise-ticket sheet does.</summary>
    public async Task<HttpResponseMessage> UploadScreenshotAsync(
        string bearer, byte[] bytes, string fileName = "screenshot.png")
    {
        ArgumentNullException.ThrowIfNull(bytes);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/support/screenshots");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.TryAddWithoutValidation(MageRideHeaders.IdempotencyKey, Guid.NewGuid().ToString());

        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "file", fileName);

        request.Content = content;

        return await Client.SendAsync(request);
    }

    public static async Task<T> OkAsync<T>(HttpResponseMessage response, string what)
    {
        ArgumentNullException.ThrowIfNull(response);

        var text = await response.Content.ReadAsStringAsync();

        Assert.True((int)response.StatusCode is >= 200 and < 300, $"{what} returned {(int)response.StatusCode}: {text}");

        return JsonSerializer.Deserialize<T>(text, MageRideJson.Options)!;
    }

    /// <summary>The RFC 7807 <c>type</c> code and the raw body of a failed response.</summary>
    public static async Task<(HttpStatusCode Status, string Code, string Body)> ProblemAsync(
        HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var text = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(text);

        var type = document.RootElement.TryGetProperty("type", out var value)
            ? value.GetString() ?? string.Empty
            : string.Empty;

        return (response.StatusCode, type.Split('/')[^1], text);
    }

    // -----------------------------------------------------------------------------------------
    // Seeding and asserting against the rows
    // -----------------------------------------------------------------------------------------

    public Task<NpgsqlConnection> OpenAsync() => _postgres.OpenAsync();

    /// <summary>An <c>iam.users</c> row, so every foreign key onto it is satisfiable.</summary>
    public async Task<Guid> CreateUserAsync(string role = "passenger")
    {
        var id = Guid.CreateVersion7();

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

    /// <summary>A passenger and a bearer for them.</summary>
    public async Task<(Guid Id, string Bearer)> CreatePassengerAsync()
    {
        var id = await CreateUserAsync("passenger");

        return (id, Tokens.Passenger(id));
    }

    /// <summary>A driver and a bearer for them.</summary>
    public async Task<(Guid Id, string Bearer)> CreateDriverAsync()
    {
        var id = await CreateUserAsync("driver");

        return (id, Tokens.Driver(id));
    }

    /// <summary>Whatever a ticket row actually says, read straight from the table.</summary>
    public async Task<IDictionary<string, object?>> TicketRowAsync(Guid ticketId)
    {
        await using var connection = await _postgres.OpenAsync();

        var row = await connection.QuerySingleAsync(
            """
            SELECT id, user_id, category, description, ride_id, screenshot_url, screenshot_upload_id,
                   status, admin_response, assigned_to, assigned_at, resolved_at, resolved_by,
                   created_at, updated_at
              FROM support.tickets WHERE id = @TicketId;
            """,
            new { TicketId = ticketId });

        return (IDictionary<string, object?>)row;
    }

    /// <summary>The whole thread, oldest first, as it is stored.</summary>
    public async Task<IReadOnlyList<(string Kind, string? From, string? To, string? Body, Guid? ActorId)>>
        ThreadAsync(Guid ticketId)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<(string, string?, string?, string?, Guid?)>(
            """
            SELECT kind, from_status, to_status, body, actor_id
              FROM support.ticket_events WHERE ticket_id = @TicketId ORDER BY at, id;
            """,
            new { TicketId = ticketId });

        return [.. rows];
    }

    /// <summary>The <c>docs.uploads</c> row an upload created.</summary>
    public async Task<(Guid OwnerId, string StorageUrl, string? Kind, DateTimeOffset? AutoDeleteAt,
        DateTimeOffset CreatedAt)> UploadAsync(Guid uploadId)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.QuerySingleAsync<(Guid, string, string?, DateTimeOffset?, DateTimeOffset)>(
            "SELECT owner_id, storage_url, kind, auto_delete_at, created_at FROM docs.uploads WHERE id = @Id;",
            new { Id = uploadId });
    }

    /// <summary>
    /// Writes a ticket the way subscription-svc's US-9.23 refund intake does — straight into the
    /// table, with none of this service's columns set.
    /// </summary>
    /// <remarks>
    /// The SQL is <c>RefundRequestRepository</c>'s, verbatim in shape, because the point of the test
    /// it serves is that a row written by <b>another service</b> still lands on the Finance queue.
    /// Reproducing it here rather than referencing subscription-svc keeps this suite from booting a
    /// second service to insert one row.
    /// </remarks>
    public async Task<Guid> SeedRefundRequestAsync(Guid driverId, string description = "Charged twice on 29 July.")
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.QuerySingleAsync<Guid>(
            """
            INSERT INTO support.tickets (user_id, category, description, ride_id)
            VALUES (@UserId, 'daily_fee_refund', @Description, NULL)
            RETURNING id;
            """,
            new { UserId = driverId, Description = description });
    }

    /// <summary>
    /// Writes a ticket the way fare-svc's AL-47 driver-QR dispute does — with its evidence in §13's
    /// original <c>screenshot_url</c>, which nothing in support-svc writes.
    /// </summary>
    /// <remarks>
    /// The SQL is <c>SupportTicketRepository</c>'s, verbatim in shape. The point of the test it
    /// serves is that a Finance-queue ticket written by another service does not reach an agent with
    /// its attachment silently dropped.
    /// </remarks>
    public async Task<Guid> SeedQrDisputeAsync(Guid driverId, string screenshotUrl)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.QuerySingleAsync<Guid>(
            """
            INSERT INTO support.tickets (user_id, category, description, ride_id, screenshot_url)
            VALUES (@UserId, 'driver_qr_dispute', 'The transfer never arrived.', NULL, @ScreenshotUrl)
            RETURNING id;
            """,
            new { UserId = driverId, ScreenshotUrl = screenshotUrl });
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

        try
        {
            if (Directory.Exists(_screenshotRoot))
            {
                Directory.Delete(_screenshotRoot, recursive: true);
            }
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"warning: could not remove {_screenshotRoot}: {exception.Message}");
        }
    }

    /// <summary>
    /// Empties what this service owns, plus the rows its tests create in other schemas.
    /// </summary>
    /// <remarks>
    /// <b>`content.faq_articles` is deliberately not truncated.</b> Migration 1902's twelve rows are
    /// the day-0 FAQ and are exactly what the language tests assert against — emptying them would
    /// leave this suite testing fixtures of its own making.
    /// </remarks>
    private static async Task ResetAsync(PostgresFixture postgres)
    {
        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            TRUNCATE support.ticket_events, support.tickets, support.command_log CASCADE;
            TRUNCATE docs.uploads CASCADE;
            TRUNCATE iam.users CASCADE;
            """);
    }
}
