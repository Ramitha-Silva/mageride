using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using MageRide.Provisioning.Bulk;
using MageRide.Provisioning.Credentials;
using MageRide.Provisioning.Endpoints;
using MageRide.Provisioning.Trackers;
using MageRide.Shared.Caching;
using MageRide.TestKit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using StackExchange.Redis;

namespace MageRide.Provisioning.Tests.Infrastructure;

/// <summary>
/// A running provisioning-svc on a real socket, against a real Postgres, Redis and Redpanda.
/// </summary>
/// <remarks>
/// Built through <see cref="ProvisioningApplication.Build"/>, so the pipeline under test — the
/// role gate, the idempotency middleware, the problem+json handler, the outbox dispatcher — is the
/// one the process runs. Kestrel rather than TestServer, for the reason C021's and C028's
/// harnesses use it: the idempotency middleware swaps the response body feature.
/// </remarks>
internal sealed class ProvisioningHarness : IAsyncDisposable
{
    /// <summary>The shared secret <c>/v1/internal/trackers/**</c> demands until C042 lands a mesh.</summary>
    public const string InternalApiKey = "c030-provisioning-internal-key-not-a-secret";

    /// <summary>Signs the bulk error-report links, so they verify across a harness restart.</summary>
    public const string ErrorReportSigningKey = "c030-error-report-signing-key-not-a-secret";

    private static int _imeiCounter = Random.Shared.Next(100_000, 900_000);
    private static int _plateCounter = Random.Shared.Next(1_000, 9_000) * 1_000;

    private readonly WebApplication _app;
    private readonly PostgresFixture _postgres;

    private readonly bool _ownsCaDirectory;

    private ProvisioningHarness(
        WebApplication app,
        HttpClient client,
        TestTokenIssuer tokens,
        PostgresFixture postgres,
        string caDirectory,
        bool ownsCaDirectory)
    {
        _app = app;
        _postgres = postgres;
        _ownsCaDirectory = ownsCaDirectory;
        Client = client;
        Tokens = tokens;
        CaDirectory = caDirectory;
    }

    public HttpClient Client { get; }

    public TestTokenIssuer Tokens { get; }

    /// <summary>Where this harness' device CA lives — <c>StepCa:RootKeyPath</c>.</summary>
    public string CaDirectory { get; }

    public IServiceProvider Services => _app.Services;

    /// <summary>A 15-digit IMEI no other test in this run will use.</summary>
    public static string NextImei() =>
        "35958" + (Interlocked.Increment(ref _imeiCounter) % 1_000_000_000)
            .ToString("D10", CultureInfo.InvariantCulture);

    /// <summary>A plate no other test in this run will use.</summary>
    public static string NextPlate() =>
        "WP-TR-" + (Interlocked.Increment(ref _plateCounter) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    public static async Task<ProvisioningHarness> StartAsync(
        PostgresFixture postgres,
        RedisFixture? redis = null,
        RedpandaFixture? redpanda = null,
        string? caDirectory = null,
        IDictionary<string, string?>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        postgres.RequireAvailable();
        await postgres.EnsureMigratedAsync();

        var tokens = new TestTokenIssuer();

        // Per harness rather than shared: a CA is process state, and a suite that reused one
        // would not notice a bind that quietly minted from a different root than the one the
        // broker trusts.
        var ca = caDirectory ?? Path.Combine(
            Path.GetTempPath(), "mageride-prov-ca-" + Guid.NewGuid().ToString("N")[..12]);

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            // The container is plain Postgres, not PgBouncer.
            ["Postgres:PgBouncerTransactionMode"] = "false",
            // Never fetched — the bearer handler is pointed at the test key below. The kernel's
            // auth wiring binds the setting all the same, so it has to be present and parseable.
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = tokens.IssuerName,
            ["Jwt:RequireHttpsMetadata"] = "false",
            // A dead address rather than an empty one, so a code path that reaches Redis without a
            // fixture fails loudly instead of connecting to whatever is on localhost.
            ["ConnectionStrings:Redis"] = redis?.ConnectionString
                                          ?? "127.0.0.1:1,abortConnect=false,connectTimeout=200,syncTimeout=200",
            ["Kafka:BootstrapServers"] = redpanda?.BootstrapServers ?? "127.0.0.1:1",
            ["Outbox:DispatcherEnabled"] = redpanda is null ? "false" : "true",
            ["Provisioning:InternalApiKey"] = InternalApiKey,
            ["Provisioning:ErrorReportSigningKey"] = ErrorReportSigningKey,
            // Both sweeps are driven a pass at a time by the tests that care, for the same reason
            // E-03's is in C029: a ticker would make every other suite's timing part of the
            // assertion.
            ["Provisioning:RotationEnabled"] = "false",
            ["Provisioning:BulkMintEnabled"] = "false",
            ["StepCa:RootKeyPath"] = ca,
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

        var app = ProvisioningApplication.Build(
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
                builder.WebHost.UseUrls("http://127.0.0.1:0");

                // PostConfigure so this runs after the kernel's AddMageRideAuth has built the
                // options. Everything else about validation — RS256 only, lifetime, issuer — is
                // left exactly as the kernel configured it, because that is what is under test.
                builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                    .PostConfigure(bearer =>
                    {
                        bearer.ConfigurationManager = null;
                        bearer.TokenValidationParameters.IssuerSigningKey = tokens.PublicKey;
                        bearer.TokenValidationParameters.IssuerSigningKeyResolver = null;
                    });
            });

        await app.StartAsync();

        var baseAddress = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();

        var client = new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(120) };

        return new ProvisioningHarness(app, client, tokens, postgres, ca, ownsCaDirectory: caDirectory is null);
    }

    // ---------------------------------------------------------------------------------------
    // HTTP
    // ---------------------------------------------------------------------------------------

    /// <summary>POSTs JSON with an <c>Idempotency-Key</c> — D3' §0 makes the header mandatory.</summary>
    public Task<HttpResponseMessage> PostAsync(string path, object? body, string? bearer, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };

        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());
        Authorize(request, bearer);

        return Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> GetAsync(string path, string? bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        Authorize(request, bearer);
        return Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> DeleteAsync(string path, string? bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, path);
        Authorize(request, bearer);
        return Client.SendAsync(request);
    }

    /// <summary>Calls a service-to-service route with the shared secret the tcp-adapter would carry.</summary>
    public Task<HttpResponseMessage> GetInternalAsync(string path, string? apiKey = InternalApiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);

        if (apiKey is not null)
        {
            request.Headers.Add(InternalTrackerEndpoints.ApiKeyHeader, apiKey);
        }

        return Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> PostInternalAsync(
        string path, object? body = null, string? apiKey = InternalApiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };

        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        if (apiKey is not null)
        {
            request.Headers.Add(InternalTrackerEndpoints.ApiKeyHeader, apiKey);
        }

        return Client.SendAsync(request);
    }

    /// <summary>Uploads a bulk CSV as the Admin Portal would — multipart, one <c>file</c> part.</summary>
    public Task<HttpResponseMessage> PostCsvAsync(
        string path, string csv, string? bearer, string? credentialType = null)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "trackers.csv");

        if (credentialType is not null)
        {
            content.Add(new StringContent(credentialType), "credentialType");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Authorize(request, bearer);

        return Client.SendAsync(request);
    }

    // ---------------------------------------------------------------------------------------
    // Seeding — provisioning-svc creates neither accounts nor vehicles; iam-svc and registry-svc do
    // ---------------------------------------------------------------------------------------

    public async Task<Guid> CreateUserAsync(string role = "driver")
    {
        var userId = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO iam.users (id, phone, role) VALUES (@Id, @Phone, @Role);",
            new
            {
                Id = userId,
                Phone = "+9477" + Random.Shared.NextInt64(1_000_000, 9_999_999).ToString(CultureInfo.InvariantCulture),
                Role = role,
            });

        return userId;
    }

    /// <summary>An APPROVED Mode C vehicle owned by <paramref name="ownerId"/>.</summary>
    public async Task<Guid> CreateVehicleAsync(Guid ownerId, string? plate = null)
    {
        var vehicleId = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO registry.vehicles
              (id, owner_id, registration_number, vehicle_type, mode, status, onboarding_status, driver_name)
            VALUES (@Id, @OwnerId, @Plate, 'three_wheeler', 'C', 'APPROVED', 'approved', 'Test Driver');
            """,
            new { Id = vehicleId, OwnerId = ownerId, Plate = plate ?? NextPlate() });

        return vehicleId;
    }

    /// <summary>A fleet with <paramref name="ownerId"/> as its owner, as the Fleet Portal (C059) would.</summary>
    public async Task<Guid> CreateFleetAsync(Guid ownerId)
    {
        var fleetId = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO registry.fleets (id, owner_id, name, status) VALUES (@Id, @OwnerId, 'Test Fleet', 'APPROVED');",
            new { Id = fleetId, OwnerId = ownerId });

        return fleetId;
    }

    /// <summary>
    /// Puts a vehicle on a fleet's roster — what a bulk CSV row resolves against.
    /// </summary>
    /// <remarks>
    /// Mode <c>B</c> because <c>registry.fleet_vehicles.mode</c> (migration 0306) admits <c>A</c>
    /// and <c>B</c> only: a fleet roster is Mode A/B operation, and a Mode C vehicle is on it as a
    /// shared private vehicle. The tracker plane does not care which — a binding is per vehicle.
    /// </remarks>
    public async Task AddToFleetAsync(Guid fleetId, Guid vehicleId)
    {
        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO registry.fleet_vehicles (fleet_id, vehicle_id, mode) VALUES (@FleetId, @VehicleId, 'B');",
            new { FleetId = fleetId, VehicleId = vehicleId });
    }

    // ---------------------------------------------------------------------------------------
    // Reads the assertions need
    // ---------------------------------------------------------------------------------------

    public Task<NpgsqlConnection> OpenAsync() => _postgres.OpenAsync();

    /// <summary>Every binding recorded for an IMEI, newest first — state and reason.</summary>
    public async Task<IReadOnlyList<(string State, string? Reason, Guid VehicleId)>> BindingsAsync(string imei)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<(string, string?, Guid)>(
            """
            SELECT state, state_reason, vehicle_id FROM prov.tracker_bindings
             WHERE imei = @Imei ORDER BY created_at DESC, id DESC;
            """,
            new { Imei = imei });

        return [.. rows];
    }

    /// <summary>The <c>(serial, revoked_at, reason)</c> of every credential minted for an IMEI.</summary>
    public async Task<IReadOnlyList<(string Serial, DateTimeOffset? RevokedAt, string? Reason)>> CertificatesAsync(
        string imei)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<(string, DateTimeOffset?, string?)>(
            """
            SELECT c.serial, c.revoked_at, c.revocation_reason
              FROM prov.device_certs c
              JOIN prov.tracker_bindings b ON b.id = c.binding_id
             WHERE b.imei = @Imei
             ORDER BY c.issued_at;
            """,
            new { Imei = imei });

        return [.. rows];
    }

    /// <summary>The rows provisioning-svc queued for <c>provisioning.events</c> (migration 0403).</summary>
    public async Task<IReadOnlyList<(string EventType, Guid AggregateId, string Payload)>> OutboxAsync(Guid vehicleId)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<(string, Guid, string)>(
            """
            SELECT event_type, aggregate_id, payload::text FROM prov.outbox
             WHERE aggregate_id = @VehicleId ORDER BY id;
            """,
            new { VehicleId = vehicleId });

        return [.. rows];
    }

    /// <summary>The cached <c>imei:{imei}</c> vehicle, or null when the entry is gone (T-03/T-12).</summary>
    public async Task<string?> CachedVehicleAsync(string imei)
    {
        var redis = Services.GetRequiredService<IConnectionMultiplexer>();
        var value = await redis.GetDatabase().StringGetAsync(RedisKeys.Imei(imei));

        return value.IsNullOrEmpty ? null : value.ToString();
    }

    /// <summary>Ages a binding so the anti-clone window has passed without waiting a day.</summary>
    public async Task AgeBindingAsync(string imei, TimeSpan by)
    {
        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            UPDATE prov.tracker_bindings
               SET created_at = created_at - @By, state_changed_at = state_changed_at - @By,
                   last_seen_at = last_seen_at - @By
             WHERE imei = @Imei;
            """,
            new { Imei = imei, By = by });
    }

    /// <summary>Brings a credential's rotation date forward so one sweep finds it.</summary>
    public async Task MakeRotationDueAsync(string imei)
    {
        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            "UPDATE prov.tracker_bindings SET rotates_at = now() - interval '1 hour' WHERE imei = @Imei;",
            new { Imei = imei });
    }

    /// <summary>Runs one rotation sweep, as the T-02 cron would.</summary>
    public Task<int> SweepRotationAsync() =>
        Services.GetRequiredService<CredentialRotationWorker>().SweepOnceAsync(CancellationToken.None);

    /// <summary>Drains the bulk mint queue until it is empty, as the T-09 worker would.</summary>
    public async Task<int> DrainBulkAsync()
    {
        var worker = Services.GetRequiredService<BulkMintWorker>();
        var total = 0;

        while (await worker.DrainOnceAsync(CancellationToken.None) is var drained && drained > 0)
        {
            total += drained;
        }

        return total;
    }

    public ICertificateAuthority Authority => Services.GetRequiredService<ICertificateAuthority>();

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>Binds a tracker and returns the 201 body, failing the test on anything else.</summary>
    public async Task<JsonElement> BindAsync(
        string bearer, string imei, Guid vehicleId, string credentialType = "x509")
    {
        var response = await PostAsync(
            "/v1/trackers/bind",
            new { imei, vehicleId = vehicleId.ToString(), method = "manual", credentialType },
            bearer);

        // The body is in the message: a bind that 500s says why in its problem+json, and a bare
        // "expected Created, got InternalServerError" sends the reader to the server log instead.
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.Created, $"bind returned {response.StatusCode}: {text}");

        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();

        // Only the directory this harness created. A caller-supplied one belongs to somebody else
        // — EmqxFixture's, most of the time — and deleting it would pull the CA out from under a
        // running broker and leave every credential the next harness mints untrusted.
        if (_ownsCaDirectory && Directory.Exists(CaDirectory))
        {
            Directory.Delete(CaDirectory, recursive: true);
        }
    }

    private static void Authorize(HttpRequestMessage request, string? bearer)
    {
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
    }
}
