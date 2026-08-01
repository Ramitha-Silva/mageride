using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
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

namespace MageRide.Registry.Tests.Infrastructure;

/// <summary>
/// A running registry-svc on a real socket, against a real Postgres.
/// </summary>
/// <remarks>
/// Built through <see cref="RegistryApplication.Build"/>, so the pipeline under test —
/// deny-by-default authorization, the idempotency middleware, the problem+json handler — is
/// the one the process runs. Kestrel rather than TestServer for the same reason C008's and
/// C020's harnesses use it: the idempotency middleware swaps the response body feature.
/// </remarks>
internal sealed class RegistryHarness : IAsyncDisposable
{
    /// <summary>The shared secret <c>/v1/internal/vehicles/**</c> demands until C042 lands a mesh.</summary>
    public const string InternalApiKey = "c028-registry-internal-key-not-a-secret";

    private static int _plateCounter = Random.Shared.Next(1_000, 9_000) * 1_000;

    private readonly WebApplication _app;
    private readonly PostgresFixture _postgres;

    private RegistryHarness(
        WebApplication app,
        HttpClient client,
        TestTokenIssuer tokens,
        PostgresFixture postgres,
        FakeDocumentExtractionClient ocr)
    {
        _app = app;
        _postgres = postgres;
        Client = client;
        Tokens = tokens;
        Ocr = ocr;
    }

    public HttpClient Client { get; }

    public TestTokenIssuer Tokens { get; }

    /// <summary>The ocr-svc stand-in (C054's seam). Configure it before the request under test.</summary>
    public FakeDocumentExtractionClient Ocr { get; }

    public IServiceProvider Services => _app.Services;

    /// <summary>A plate no other test in this run will use.</summary>
    public static string NextPlate() =>
        "WP-QA-" + (Interlocked.Increment(ref _plateCounter) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    public static Task<RegistryHarness> StartAsync(
        PostgresFixture postgres, IDictionary<string, string?>? settings = null) =>
        StartAsync(postgres, Environments.Development, settings);

    public static async Task<RegistryHarness> StartAsync(
        PostgresFixture postgres, string environmentName, IDictionary<string, string?>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        postgres.RequireAvailable();
        await postgres.EnsureMigratedAsync();

        var tokens = new TestTokenIssuer();

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
            // C028. Redis and Redpanda are both optional to a registry-svc *test*: the D-03 lock
            // is best-effort (Postgres holds the invariant) and the outbox dispatcher is off
            // unless a test asks for it, so the C021 classes still run on Postgres alone. A dead
            // address rather than an empty one, so a code path that reaches either fails loudly
            // instead of connecting to whatever happens to be on localhost.
            ["ConnectionStrings:Redis"] = "127.0.0.1:1,abortConnect=false,connectTimeout=200,syncTimeout=200",
            ["Kafka:BootstrapServers"] = "127.0.0.1:1",
            ["Outbox:DispatcherEnabled"] = "false",
            ["Registry:InternalApiKey"] = InternalApiKey,
            // C029. E-03's sweep is driven a tick at a time by the tests that care, for the same
            // reason dispatch-svc's offer backstop is: a ticker would make every other suite's
            // timing part of the assertion.
            ["Registry:DocumentExpiryEnabled"] = "false",
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

        var ocr = new FakeDocumentExtractionClient();

        var app = RegistryApplication.Build(
            new WebApplicationOptions
            {
                EnvironmentName = environmentName,
                ContentRootPath = AppContext.BaseDirectory,
            },
            builder =>
            {
                // Ahead of AddRegistryServices, whose TryAddSingleton then stands down — the same
                // registration order ocr-svc (C054) will use in the composed deployment.
                builder.Services.AddSingleton<MageRide.Registry.Onboarding.IDocumentExtractionClient>(ocr);

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

        var baseAddress = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();

        var client = new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(60) };

        return new RegistryHarness(app, client, tokens, postgres, ocr);
    }

    /// <summary>
    /// Creates the <c>iam.users</c> row a vehicle's <c>owner_id</c> foreign key needs, and
    /// returns a driver bearer for it. registry-svc never creates accounts — iam-svc does.
    /// </summary>
    public async Task<Guid> CreateDriverAsync()
    {
        var driverId = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO iam.users (id, phone, role) VALUES (@Id, @Phone, 'driver');",
            new { Id = driverId, Phone = "+9477" + Random.Shared.NextInt64(1_000_000, 9_999_999).ToString(CultureInfo.InvariantCulture) });

        return driverId;
    }

    public Task<NpgsqlConnection> OpenAsync() => _postgres.OpenAsync();

    /// <summary>
    /// POSTs JSON with an <c>Idempotency-Key</c>. A fresh key unless the caller supplies one —
    /// D3' §0 makes the header mandatory, so omitting it by accident would test the 400 path.
    /// </summary>
    public Task<HttpResponseMessage> PostAsync(
        string path, object? body, string? bearer, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body ?? new { }),
        };

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

    /// <summary>PUTs JSON. No <c>Idempotency-Key</c> — D3' §0 requires it on POST only.</summary>
    public Task<HttpResponseMessage> PutAsync(string path, object? body, string? bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = JsonContent.Create(body ?? new { }),
        };

        Authorize(request, bearer);
        return Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> DeleteAsync(string path, string? bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, path);
        Authorize(request, bearer);
        return Client.SendAsync(request);
    }

    /// <summary>POSTs to a service-to-service route with the shared secret fare-svc would carry.</summary>
    public Task<HttpResponseMessage> PostInternalAsync(string path, object? body, string? apiKey = InternalApiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body ?? new { }),
        };

        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        if (apiKey is not null)
        {
            request.Headers.Add(Registry.Endpoints.InternalVehicleEndpoints.ApiKeyHeader, apiKey);
        }

        return Client.SendAsync(request);
    }

    /// <summary>
    /// Creates a fleet and assigns <paramref name="driverId"/> to <paramref name="vehicleId"/> —
    /// the US-13.9 "temporarily assigned" state. fleet-svc (C059) owns writing these; registry-svc
    /// only reads them through the eligibility projection.
    /// </summary>
    public async Task<Guid> AssignToFleetAsync(Guid vehicleId, Guid driverId, Guid fleetOwnerId)
    {
        var fleetId = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO registry.fleets (id, owner_id, name, status)
            VALUES (@FleetId, @OwnerId, 'Test Fleet', 'APPROVED');

            INSERT INTO registry.fleet_vehicles (fleet_id, vehicle_id, mode)
            VALUES (@FleetId, @VehicleId, 'B');

            INSERT INTO registry.fleet_assignments (fleet_id, vehicle_id, driver_id)
            VALUES (@FleetId, @VehicleId, @DriverId);
            """,
            new { FleetId = fleetId, OwnerId = fleetOwnerId, VehicleId = vehicleId, DriverId = driverId });

        return fleetId;
    }

    /// <summary>Revokes every assignment a driver holds — US-13.8's "immediately loses the ability".</summary>
    public async Task RevokeAssignmentsAsync(Guid driverId)
    {
        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            "UPDATE registry.fleet_assignments SET revoked_at = now() WHERE driver_id = @DriverId;",
            new { DriverId = driverId });
    }

    /// <summary>
    /// Registers a vehicle in a mode the Driver App refuses, as the Fleet Portal (C059) would.
    /// </summary>
    public async Task<Guid> SeedFleetVehicleAsync(
        Guid ownerId, string mode = "B", string vehicleType = "van", string status = "APPROVED")
    {
        var vehicleId = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO registry.vehicles
              (id, owner_id, registration_number, vehicle_type, mode, status, onboarding_status, driver_name)
            VALUES
              (@Id, @OwnerId, @Plate, @VehicleType, @Mode, @Status,
               CASE WHEN @Status = 'APPROVED' THEN 'approved' ELSE 'incomplete' END, 'Fleet Driver');
            """,
            new { Id = vehicleId, OwnerId = ownerId, Plate = NextPlate(), VehicleType = vehicleType, Mode = mode, Status = status });

        return vehicleId;
    }

    /// <summary>An active <c>subscription.grants</c> row, as subscription-svc would leave it (AL-23).</summary>
    public async Task<Guid> SeedSubscriptionGrantAsync(Guid vehicleId, Guid passengerId)
    {
        var grantId = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO subscription.grants (id, vehicle_id, passenger_id, status)
            VALUES (@Id, @VehicleId, @PassengerId, 'active');
            """,
            new { Id = grantId, VehicleId = vehicleId, PassengerId = passengerId });

        return grantId;
    }

    /// <summary>Grants a Mode B share and has the grantee accept it — a live grant (US-4.1/4.3b).</summary>
    public async Task<string> GrantShareAsync(string vehicleId, Guid granteeId, string ownerBearer)
    {
        var created = await PostAsync($"/v1/vehicles/{vehicleId}/share", new { userId = granteeId.ToString() }, ownerBearer);
        Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);

        var grantId = (await ReadJsonAsync(created)).GetProperty("grantId").GetString()!;

        var accepted = await PostAsync(
            $"/v1/vehicles/{vehicleId}/share/{grantId}/accept", null, Tokens.Driver(granteeId));
        Assert.Equal(System.Net.HttpStatusCode.OK, accepted.StatusCode);

        return grantId;
    }

    /// <summary>Suspends a vehicle from dispatch, as the E-03 document-expiry job (C029) would.</summary>
    public async Task SuspendDispatchAsync(Guid vehicleId)
    {
        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            "UPDATE registry.vehicles SET dispatch_state = 'DISPATCH_SUSPENDED' WHERE id = @Id;",
            new { Id = vehicleId });
    }

    /// <summary>How many vehicles the driver has selected. US-9.6 makes the answer 0 or 1, always.</summary>
    public async Task<int> ActiveSelectionCountAsync(Guid driverId)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<int>(
            """
            SELECT count(*) FROM registry.driver_profiles
             WHERE driver_id = @DriverId AND active_vehicle_id IS NOT NULL;
            """,
            new { DriverId = driverId });
    }

    /// <summary>The vehicle published into <c>lock:driver:{driverId}</c> for the downstream planes (D-03).</summary>
    public async Task<string?> PublishedLiveVehicleAsync(Guid driverId)
    {
        var redis = Services.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>();

        var value = await redis.GetDatabase()
            .StringGetAsync(MageRide.Shared.Caching.RedisKeys.DriverLiveVehicle(driverId));

        return value.IsNullOrEmpty ? null : value.ToString();
    }

    // Δ AL-57 — `MerchantIdAsync` removed with `registry.driver_payouts` (migration 1010).


    /// <summary>The rows registry-svc queued for <c>registry.events</c> (migration 0309).</summary>
    public async Task<IReadOnlyList<(string EventType, Guid AggregateId, string Payload)>> OutboxAsync(Guid vehicleId)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<(string, Guid, string)>(
            """
            SELECT event_type, aggregate_id, payload::text
              FROM registry.outbox
             WHERE aggregate_id = @VehicleId
             ORDER BY id;
            """,
            new { VehicleId = vehicleId });

        return [.. rows];
    }

    /// <summary>
    /// Seeds the <c>docs.uploads</c> row a document step needs, as the upload surface would. No
    /// service owns that table yet; registry-svc only reads it to resolve a file id to its bytes.
    /// </summary>
    public async Task<string> SeedUploadAsync(Guid ownerId, string kind = "insurance")
    {
        var uploadId = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO docs.uploads (id, owner_id, storage_url, kind, captured_via)
            VALUES (@Id, @OwnerId, @Url, @Kind, 'camera_dragcrop');
            """,
            new { Id = uploadId, OwnerId = ownerId, Url = $"s3://mageride-docs/{uploadId}.jpg", Kind = kind });

        return uploadId.ToString();
    }

    /// <summary>Walks Profile Setup with clean uploads — AL-27's phase 1, which precedes any vehicle.</summary>
    public async Task<JsonElement> CompleteProfileSetupAsync(
        Guid driverId, string bearer, string driverName = "Nimal Perera", object? overrides = null)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["driverName"] = driverName,
            ["profilePhotoFileId"] = await SeedUploadAsync(driverId, "profile_photo"),
            ["licenseFrontFileId"] = await SeedUploadAsync(driverId, "driving_license"),
            ["licenseBackFileId"] = await SeedUploadAsync(driverId, "driving_license"),
        };

        if (overrides is not null)
        {
            foreach (var property in JsonSerializer.SerializeToElement(overrides).EnumerateObject())
            {
                body[property.Name] = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value;
            }
        }

        var response = await PutAsync("/v1/drivers/profile", body, bearer);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        return await ReadJsonAsync(response);
    }

    /// <summary>Saves one onboarding step, seeding whatever uploads it needs.</summary>
    public async Task<HttpResponseMessage> SaveStepAsync(
        Guid driverId, string bearer, string vehicleId, string step, object? extra = null)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (step != "details")
        {
            body["fileId"] = await SeedUploadAsync(driverId, step);

            if (step == "photos")
            {
                body["fileIdBack"] = await SeedUploadAsync(driverId, step);
            }
        }

        if (extra is not null)
        {
            foreach (var property in JsonSerializer.SerializeToElement(extra).EnumerateObject())
            {
                body[property.Name] = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value;
            }
        }

        return await PutAsync($"/v1/vehicles/{vehicleId}/onboarding/{step}", body, bearer);
    }

    /// <summary>Saves the three document steps and asserts each was accepted.</summary>
    public async Task<JsonElement> CompleteOnboardingAsync(Guid driverId, string bearer, string vehicleId)
    {
        JsonElement last = default;

        foreach (var step in new[] { "insurance", "revenue", "photos" })
        {
            var response = await SaveStepAsync(driverId, bearer, vehicleId, step);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            last = await ReadJsonAsync(response);
        }

        return last;
    }

    /// <summary>Runs one E-03 sweep, as tonight's job would. Returns how many notices it emitted.</summary>
    public Task<int> SweepDocumentExpiryAsync() =>
        Services.GetRequiredService<MageRide.Registry.Onboarding.DocumentExpiryWorker>()
            .SweepOnceAsync(CancellationToken.None);

    /// <summary>Rewrites a document's expiry, as a certificate ageing would.</summary>
    public async Task SetDocumentExpiryAsync(Guid vehicleId, string kind, DateTimeOffset expiresAt)
    {
        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            UPDATE registry.documents SET expires_at = @ExpiresAt
             WHERE vehicle_id = @VehicleId AND kind = @Kind;
            """,
            new { VehicleId = vehicleId, Kind = kind, ExpiresAt = expiresAt });
    }

    /// <summary>The <c>(kind, status, expires_at)</c> of every document on a vehicle, newest first.</summary>
    public async Task<IReadOnlyList<(string Kind, string Status, DateTimeOffset? ExpiresAt)>> DocumentsAsync(Guid vehicleId)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<(string, string, DateTimeOffset?)>(
            """
            SELECT kind, status, expires_at FROM registry.documents
             WHERE vehicle_id = @VehicleId ORDER BY created_at DESC, id DESC;
            """,
            new { VehicleId = vehicleId });

        return [.. rows];
    }

    /// <summary>Every <c>(field_key, source, verify_status)</c> recorded for a driver's documents.</summary>
    public async Task<IReadOnlyList<(string Key, string Source, string VerifyStatus)>> DriverFieldsAsync(Guid driverId)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<(string, string, string)>(
            """
            SELECT f.field_key, f.source, f.verify_status
              FROM registry.document_fields f
              JOIN registry.documents d ON d.id = f.document_id
             WHERE d.driver_id = @DriverId AND d.vehicle_id IS NULL
             ORDER BY f.created_at, f.id;
            """,
            new { DriverId = driverId });

        return [.. rows];
    }

    /// <summary>The vehicle's dispatch state — E-03's gate (US-9.6 via the eligibility view).</summary>
    public async Task<string> DispatchStateAsync(Guid vehicleId)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.QuerySingleAsync<string>(
            "SELECT dispatch_state FROM registry.vehicles WHERE id = @Id;", new { Id = vehicleId });
    }

    /// <summary>Confirms every pending field on a vehicle, as a Verification Officer would (C062).</summary>
    public async Task<int> ConfirmPendingFieldsAsync(Guid vehicleId, Guid officerId)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.ExecuteAsync(
            """
            UPDATE registry.document_fields f
               SET verify_status = 'confirmed', confirmed_by = @OfficerId, confirmed_at = now()
              FROM registry.documents d
             WHERE d.id = f.document_id AND d.vehicle_id = @VehicleId AND f.verify_status = 'pending';
            """,
            new { VehicleId = vehicleId, OfficerId = officerId });
    }

    /// <summary>Registers a vehicle and returns the 201 body, failing the test on anything else.</summary>
    public async Task<JsonElement> RegisterVehicleAsync(
        string bearer, string? plate = null, string vehicleType = "three_wheeler", string driverName = "Test Driver")
    {
        var response = await PostAsync(
            "/v1/vehicles",
            new { registrationNumber = plate ?? NextPlate(), vehicleType, mode = "C", driverName },
            bearer);

        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    /// <summary>Registers a vehicle and approves it through the dev seed path.</summary>
    public async Task<string> RegisterApprovedVehicleAsync(
        string bearer, string? plate = null, string vehicleType = "three_wheeler")
    {
        var vehicleId = (await RegisterVehicleAsync(bearer, plate, vehicleType)).GetProperty("vehicleId").GetString()!;

        var approved = await PostAsync($"/v1/dev/vehicles/{vehicleId}/approve", null, bearer);
        Assert.Equal(System.Net.HttpStatusCode.OK, approved.StatusCode);

        return vehicleId;
    }

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static void Authorize(HttpRequestMessage request, string? bearer)
    {
        if (bearer is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        }
    }
}
